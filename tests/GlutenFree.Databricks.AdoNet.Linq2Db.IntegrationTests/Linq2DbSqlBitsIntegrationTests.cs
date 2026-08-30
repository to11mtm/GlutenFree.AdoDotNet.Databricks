using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.Databricks.AdoNet.Linq2Db;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.IntegrationTests;

/// <summary>
/// Live coverage for the remaining "SQL bits" and mapping-schema items from
/// planning/linq2db-dataprovider.md: inline selects, all join types, GROUP BY,
/// subqueries, IN/NOT IN, UPDATE/DELETE, and literal rendering of every mapped type.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Linq2DbSqlBitsIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private readonly string _schema = IntegrationConfig.CreateSchemaName("l2sql");
    private DatabricksConnection _connection = null!;

    private sealed class Customer
    {
        [Column("id"), PrimaryKey] public long Id { get; set; }

        [Column("name")] public string? Name { get; set; }
    }

    private sealed class Order
    {
        [Column("id"), PrimaryKey] public long Id { get; set; }

        [Column("customer_id")] public long CustomerId { get; set; }

        [Column("amount")] public decimal Amount { get; set; }
    }

    private sealed class TypeRow
    {
        [Column("id"), PrimaryKey] public long Id { get; set; }

        [Column("flag")] public bool Flag { get; set; }

        [Column("ts")] public DateTime Ts { get; set; }

        [Column("day")] public DateOnly Day { get; set; }

        [Column("price")] public decimal Price { get; set; }

        [Column("name")] public string? Name { get; set; }

        [Column("blob")] public byte[]? Blob { get; set; }

        [Column("gid")] public Guid Gid { get; set; }
    }

    private ITable<Customer> Customers(DataConnection db)
        => db.GetTable<Customer>().TableName("t_customers").SchemaName(_schema).ServerName(Catalog);

    private ITable<Order> Orders(DataConnection db)
        => db.GetTable<Order>().TableName("t_orders").SchemaName(_schema).ServerName(Catalog);

    private ITable<TypeRow> Types(DataConnection db)
        => db.GetTable<TypeRow>().TableName("t_types").SchemaName(_schema).ServerName(Catalog);

    public async Task InitializeAsync()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            return;
        }

        _connection = new DatabricksConnection(IntegrationConfig.ConnectionString);
        await _connection.OpenAsync();
        await IntegrationConfig.SweepStaleSchemasAsync(_connection);
        await ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {Catalog}.{_schema}");
        await ExecuteAsync($"CREATE TABLE {Catalog}.{_schema}.t_customers (id BIGINT, name STRING)");
        await ExecuteAsync($"CREATE TABLE {Catalog}.{_schema}.t_orders (id BIGINT, customer_id BIGINT, amount DECIMAL(10,2))");
        await ExecuteAsync(
            $"CREATE TABLE {Catalog}.{_schema}.t_types " +
            "(id BIGINT, flag BOOLEAN, ts TIMESTAMP, day DATE, price DECIMAL(10,2), name STRING, blob BINARY, gid STRING)");

        // alice has orders, bob has none; order 30 references a missing customer (id 99).
        await ExecuteAsync($"INSERT INTO {Catalog}.{_schema}.t_customers VALUES (1, 'alice'), (2, 'bob')");
        await ExecuteAsync(
            $"INSERT INTO {Catalog}.{_schema}.t_orders VALUES (10, 1, 5.00), (11, 1, 15.00), (30, 99, 7.50)");
    }

    public async Task DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await ExecuteAsync($"DROP SCHEMA IF EXISTS {Catalog}.{_schema} CASCADE");
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [IntegrationFact]
    public void SelectQuery_inline_select_roundtrips()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var row = db.SelectQuery(() => new
        {
            foo = Sql.AsSql("a"),
            bar = Sql.AsSql(1),
            when = Sql.AsSql(new DateOnly(2026, 8, 29)),
        }).Single();

        Assert.Equal(("a", 1, new DateOnly(2026, 8, 29)), (row.foo, row.bar, row.when));
    }

    [IntegrationFact]
    public void Inner_join_matches_only_related_rows()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var rows =
            (from o in Orders(db)
             join c in Customers(db) on o.CustomerId equals c.Id
             orderby o.Id
             select new { c.Name, o.Amount }).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("alice", r.Name));
    }

    [IntegrationFact]
    public void Left_join_keeps_unmatched_left_rows()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var rows =
            (from o in Orders(db)
             from c in Customers(db).LeftJoin(c => c.Id == o.CustomerId)
             orderby o.Id
             select new { o.Id, c.Name }).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Null(rows.Single(r => r.Id == 30).Name); // order without customer survives
    }

    [IntegrationFact]
    public void Right_join_keeps_unmatched_right_rows()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var rows =
            (from o in Orders(db)
             from c in Customers(db).RightJoin(c => c.Id == o.CustomerId)
             select new { CustomerName = c.Name, OrderId = (long?)o.Id }).ToList();

        // bob has no orders but must be present.
        Assert.Contains(rows, r => r.CustomerName == "bob" && r.OrderId is null or 0);
    }

    [IntegrationFact]
    public void Full_join_keeps_unmatched_rows_from_both_sides()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var rows =
            (from o in Orders(db)
             from c in Customers(db).FullJoin(c => c.Id == o.CustomerId)
             select new { CustomerName = c.Name, OrderId = (long?)o.Id }).ToList();

        Assert.Equal(4, rows.Count); // 2 matched + orphan order + orphan customer
        Assert.Contains(rows, r => r.CustomerName == "bob");
        Assert.Contains(rows, r => r.CustomerName is null && r.OrderId == 30);
    }

    [IntegrationFact]
    public void Cross_join_produces_cartesian_product()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var count =
            (from o in Orders(db)
             from c in Customers(db)
             select new { o.Id, c.Name }).Count();

        Assert.Equal(6, count); // 3 orders x 2 customers
    }

    [IntegrationFact]
    public void Group_by_aggregates_per_key()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var groups = Orders(db)
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.Amount), Count = g.Count() })
            .OrderBy(g => g.CustomerId)
            .ToList();

        Assert.Equal(2, groups.Count);
        Assert.Equal((1L, 20.00m, 2), (groups[0].CustomerId, groups[0].Total, groups[0].Count));
        Assert.Equal((99L, 7.50m, 1), (groups[1].CustomerId, groups[1].Total, groups[1].Count));
    }

    [IntegrationFact]
    public void Exists_subquery_filters_rows()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var withOrders = Customers(db)
            .Where(c => Orders(db).Any(o => o.CustomerId == c.Id))
            .Select(c => c.Name)
            .ToList();

        Assert.Equal(["alice"], withOrders);
    }

    [IntegrationFact]
    public void In_and_not_in_translate_and_filter()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);
        var ids = new long[] { 10, 30 };

        var inRows = Orders(db).Where(o => ids.Contains(o.Id)).Select(o => o.Id).OrderBy(x => x).ToList();
        var notInRows = Orders(db).Where(o => !ids.Contains(o.Id)).Select(o => o.Id).ToList();

        Assert.Equal([10L, 30L], inRows);
        Assert.Equal([11L], notInRows);
    }

    [IntegrationFact]
    public void Update_and_delete_roundtrip()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);
        Orders(db).Insert(() => new Order { Id = 500, CustomerId = 2, Amount = 1.00m });

        Orders(db)
            .Where(o => o.Id == 500L)
            .Set(o => o.Amount, 2.50m)
            .Update();
        Assert.Equal(2.50m, Orders(db).Single(o => o.Id == 500L).Amount);

        Orders(db).Where(o => o.Id == 500L).Delete();
        Assert.Empty(Orders(db).Where(o => o.Id == 500L).ToList());
    }

    [IntegrationFact]
    public void MappingSchema_literals_roundtrip_all_types()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);
        // Inline parameters force every value through the mapping schema's
        // SetValueToSqlConverter literal rendering (TIMESTAMP '...', X'...', TRUE, escaping).
        db.InlineParameters = true;

        var original = new TypeRow
        {
            Id = 1,
            Flag = true,
            Ts = new DateTime(2026, 8, 29, 12, 34, 56, 789, DateTimeKind.Utc),
            Day = new DateOnly(2026, 8, 29),
            Price = 1234.56m,
            Name = "it's a \\ backslash 'quote' test",
            Blob = [0xDE, 0xAD, 0xBE, 0xEF],
            Gid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        };

        Types(db).Insert(() => new TypeRow
        {
            Id = original.Id,
            Flag = original.Flag,
            Ts = original.Ts,
            Day = original.Day,
            Price = original.Price,
            Name = original.Name,
            Blob = original.Blob,
            Gid = original.Gid,
        });

        var stored = Types(db).Single(t => t.Id == 1L);

        Assert.True(stored.Flag);
        Assert.Equal(original.Ts, stored.Ts);
        Assert.Equal(original.Day, stored.Day);
        Assert.Equal(original.Price, stored.Price);
        Assert.Equal(original.Name, stored.Name);
        Assert.Equal(original.Blob, stored.Blob);
        Assert.Equal(original.Gid, stored.Gid);
    }
}
