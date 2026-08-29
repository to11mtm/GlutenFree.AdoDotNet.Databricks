using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.Tests;
using GlutenFree.Databricks.AdoNet.Transport;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Tests;

/// <summary>
/// SQL-generation coverage for the constructs listed in planning/linq2db-dataprovider.md:
/// inline selects, window functions, joins, grouping, subqueries, CTEs, IN, bulk copy, MERGE.
/// </summary>
public class Linq2DbSqlGenerationTests
{
    [Table("orders")]
    private sealed class Order
    {
        [Column("id"), PrimaryKey] public long Id { get; set; }

        [Column("customer_id")] public long CustomerId { get; set; }

        [Column("amount")] public decimal Amount { get; set; }
    }

    [Table("customers")]
    private sealed class Customer
    {
        [Column("id"), PrimaryKey] public long Id { get; set; }

        [Column("name")] public string? Name { get; set; }
    }

    private static (DataConnection Db, FakeTransport Transport) CreateDb()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        var db = DatabricksTools.CreateDataConnection(connection);
        return (db, transport);
    }

    /// <summary>Empty result with the given string-typed columns, enough for 0-row materialization.</summary>
    private static StatementResponse EmptyResult(params string[] columns) => new()
    {
        StatementId = "stmt-1",
        Status = new StatementStatus { State = "SUCCEEDED" },
        Manifest = new ResultManifest
        {
            Format = "JSON_ARRAY",
            TotalChunkCount = 0,
            TotalRowCount = 0,
            Schema = new ResultSchema
            {
                ColumnCount = columns.Length,
                Columns = columns
                    .Select((name, i) => new ColumnInfo { Name = name, TypeName = "STRING", Position = i })
                    .ToArray(),
            },
        },
    };

    private static string GetSql(FakeTransport transport)
        => string.Join(' ', Assert.Single(transport.ExecutedRequests).Statement
            .Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void SelectQuery_produces_inline_select()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = new StatementResponse
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "JSON_ARRAY",
                TotalChunkCount = 1,
                TotalRowCount = 1,
                Schema = new ResultSchema
                {
                    ColumnCount = 2,
                    Columns =
                    [
                        new ColumnInfo { Name = "foo", TypeName = "STRING", Position = 0 },
                        new ColumnInfo { Name = "bar", TypeName = "INT", Position = 1 },
                    ],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [["a", "1"]] },
        };

        var row = db.SelectQuery(() => new { foo = Sql.AsSql("a"), bar = Sql.AsSql(1) }).Single();

        Assert.Equal(("a", 1), (row.foo, row.bar));
        var sql = GetSql(transport);
        Assert.StartsWith("SELECT", sql);
    }

    [Fact]
    public void Window_function_generates_over_clause()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("id", "rn");

        db.GetTable<Order>()
            .Select(o => new
            {
                o.Id,
                Rank = Sql.Ext.RowNumber().Over().PartitionBy(o.CustomerId).OrderBy(o.Amount).ToValue(),
            })
            .ToList();

        var sql = GetSql(transport);
        Assert.Contains("ROW_NUMBER() OVER", sql);
        Assert.Contains("PARTITION BY", sql);
        Assert.Contains("ORDER BY", sql);
    }

    [Fact]
    public void Inner_join_generates_inner_join()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("name", "amount");

        (from o in db.GetTable<Order>()
         join c in db.GetTable<Customer>() on o.CustomerId equals c.Id
         select new { c.Name, o.Amount }).ToList();

        Assert.Contains("INNER JOIN `customers`", GetSql(transport));
    }

    [Fact]
    public void Left_join_generates_left_join()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("name", "amount");

        (from o in db.GetTable<Order>()
         from c in db.GetTable<Customer>().LeftJoin(c => c.Id == o.CustomerId)
         select new { c.Name, o.Amount }).ToList();

        Assert.Contains("LEFT JOIN `customers`", GetSql(transport));
    }

    [Fact]
    public void Right_join_generates_right_join()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("name", "amount");

        (from o in db.GetTable<Order>()
         from c in db.GetTable<Customer>().RightJoin(c => c.Id == o.CustomerId)
         select new { c.Name, o.Amount }).ToList();

        Assert.Contains("RIGHT JOIN `customers`", GetSql(transport));
    }

    [Fact]
    public void Full_join_generates_full_join()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("name", "amount");

        (from o in db.GetTable<Order>()
         from c in db.GetTable<Customer>().FullJoin(c => c.Id == o.CustomerId)
         select new { c.Name, o.Amount }).ToList();

        Assert.Contains("FULL JOIN `customers`", GetSql(transport));
    }

    [Fact]
    public void Cross_join_generates_cross_join()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("name", "amount");

        (from o in db.GetTable<Order>()
         from c in db.GetTable<Customer>()
         select new { c.Name, o.Amount }).ToList();

        Assert.Contains("CROSS JOIN `customers`", GetSql(transport));
    }

    [Fact]
    public void Group_by_generates_group_by()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("customer_id", "total");

        db.GetTable<Order>()
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.Amount) })
            .ToList();

        var sql = GetSql(transport);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("Sum(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exists_subquery_generates_exists()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("id", "name");

        db.GetTable<Customer>()
            .Where(c => db.GetTable<Order>().Any(o => o.CustomerId == c.Id && o.Amount > 100m))
            .ToList();

        Assert.Contains("EXISTS", GetSql(transport));
    }

    [Fact]
    public void Cte_generates_with_clause()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("id", "customer_id", "amount");

        var bigOrders = db.GetTable<Order>().Where(o => o.Amount > 100m).AsCte("big_orders");
        bigOrders.Where(o => o.CustomerId == 1L).ToList();

        var sql = GetSql(transport);
        Assert.StartsWith("WITH", sql);
        Assert.Contains("`big_orders`", sql);
    }

    [Fact]
    public void Contains_generates_in_clause()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("id", "customer_id", "amount");

        var ids = new long[] { 1, 2, 3 };
        db.GetTable<Order>().Where(o => ids.Contains(o.CustomerId)).ToList();

        Assert.Contains("IN (", GetSql(transport));
    }

    [Fact]
    public void Not_contains_generates_not_in_clause()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("id", "customer_id", "amount");

        var ids = new long[] { 1, 2 };
        db.GetTable<Order>().Where(o => !ids.Contains(o.CustomerId)).ToList();

        Assert.Contains("NOT IN (", GetSql(transport));
    }

    [Fact]
    public void BulkCopy_multiple_rows_generates_multi_row_insert()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = Responses.EmptySuccess;

        db.BulkCopy(
            new BulkCopyOptions { BulkCopyType = BulkCopyType.MultipleRows },
            new[]
            {
                new Order { Id = 1, CustomerId = 10, Amount = 1m },
                new Order { Id = 2, CustomerId = 20, Amount = 2m },
                new Order { Id = 3, CustomerId = 30, Amount = 3m },
            });

        var sql = GetSql(transport);
        Assert.StartsWith("INSERT INTO `orders`", sql);
        var valuesIndex = sql.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase);
        Assert.True(valuesIndex >= 0, $"No VALUES clause in: {sql}");
        Assert.Equal(3, sql[valuesIndex..].Count(c => c == '('));
    }

    [Fact]
    public void Correlated_take_subquery_generates_lateral_join()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = EmptyResult("name", "amount");

        // Correlated subquery with Take forces an APPLY join, which the Databricks
        // builder must emit as INNER JOIN LATERAL (no CROSS APPLY in Databricks SQL).
        (from c in db.GetTable<Customer>()
         from o in db.GetTable<Order>()
             .Where(o => o.CustomerId == c.Id)
             .OrderByDescending(o => o.Amount)
             .Take(1)
         select new { c.Name, o.Amount }).ToList();

        var sql = GetSql(transport);
        Assert.Contains("JOIN LATERAL", sql);
        Assert.DoesNotContain("APPLY", sql);
    }

    [Fact]
    public void Merge_generates_merge_into()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = Responses.EmptySuccess;

        db.GetTable<Order>()
            .Merge()
            .Using(new[] { new Order { Id = 1, CustomerId = 10, Amount = 5m } })
            .OnTargetKey()
            .UpdateWhenMatched()
            .InsertWhenNotMatched()
            .Merge();

        var sql = GetSql(transport);
        Assert.StartsWith("MERGE INTO `orders`", sql);
        Assert.Contains("WHEN MATCHED THEN UPDATE", sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", sql);
    }
}
