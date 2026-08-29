using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.Databricks.AdoNet.Linq2Db;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.IntegrationTests;

/// <summary>
/// Live validation of the dialect constructs from planning/linq2db-dataprovider.md:
/// LATERAL joins, MERGE, bulk copy, window functions, and CTEs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Linq2DbDialectIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private readonly string _schema = $"adonet_l2dbx_{Guid.NewGuid():N}";
    private DatabricksConnection _connection = null!;

    private sealed class Sale
    {
        [Column("id"), PrimaryKey] public long Id { get; set; }

        [Column("region")] public string? Region { get; set; }

        [Column("amount")] public decimal Amount { get; set; }
    }

    private ITable<Sale> GetSales(DataConnection db)
        => db.GetTable<Sale>().TableName("sales").SchemaName(_schema).ServerName(Catalog);

    public async Task InitializeAsync()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            return;
        }

        _connection = new DatabricksConnection(IntegrationConfig.ConnectionString);
        await _connection.OpenAsync();
        await ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {Catalog}.{_schema}");
        await ExecuteAsync($"CREATE TABLE {Catalog}.{_schema}.sales (id BIGINT, region STRING, amount DECIMAL(10,2))");
        await ExecuteAsync(
            $"INSERT INTO {Catalog}.{_schema}.sales VALUES " +
            "(1, 'east', 10.00), (2, 'east', 30.00), (3, 'west', 20.00), (4, 'west', 5.00)");
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
    public void Lateral_join_returns_top_sale_per_region()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var topPerRegion =
            (from r in GetSales(db).Select(s => s.Region).Distinct()
             from top in GetSales(db)
                 .Where(s => s.Region == r)
                 .OrderByDescending(s => s.Amount)
                 .Take(1)
             orderby top.Region
             select new { top.Region, top.Amount }).ToList();

        Assert.Equal(2, topPerRegion.Count);
        Assert.Equal(("east", 30.00m), (topPerRegion[0].Region, topPerRegion[0].Amount));
        Assert.Equal(("west", 20.00m), (topPerRegion[1].Region, topPerRegion[1].Amount));
        // Guard against linq2db reducing the correlated subquery to another shape:
        // this test must actually exercise the INNER JOIN LATERAL emission.
        Assert.Contains("INNER JOIN LATERAL", db.LastQuery);
    }

    [IntegrationFact]
    public void Left_lateral_join_keeps_rows_without_matches()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        // Top sale over 25.00 per region: east qualifies (30.00), west has none —
        // the LEFT JOIN LATERAL emission must keep west with NULLs. Selecting two
        // subquery columns forces the OuterApply (LEFT LATERAL) join shape.
        var results =
            (from r in GetSales(db).Select(s => s.Region).Distinct()
             from top in GetSales(db)
                 .Where(s => s.Region == r && s.Amount > 25.00m)
                 .OrderByDescending(s => s.Amount)
                 .Take(1)
                 .DefaultIfEmpty()
             orderby r
             select new { Region = r, TopId = (long?)top!.Id, TopAmount = (decimal?)top.Amount }).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(("east", 2L, 30.00m), (results[0].Region, results[0].TopId, results[0].TopAmount));
        Assert.Equal("west", results[1].Region);
        Assert.Null(results[1].TopId);
        Assert.Null(results[1].TopAmount);
        // Guard against linq2db reducing the correlated subquery to another shape:
        // this test must actually exercise the LEFT JOIN LATERAL emission.
        Assert.Contains("LEFT JOIN LATERAL", db.LastQuery);
    }

    [IntegrationFact]
    public void Window_function_ranks_rows()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var ranked = GetSales(db)
            .Select(s => new
            {
                s.Id,
                Rank = Sql.Ext.RowNumber().Over().PartitionBy(s.Region).OrderByDesc(s.Amount).ToValue(),
            })
            .Where(x => x.Rank == 1)
            .OrderBy(x => x.Id)
            .ToList();

        Assert.Equal([2L, 3L], ranked.Select(x => x.Id));
    }

    [IntegrationFact]
    public void Cte_filters_through_with_clause()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var bigSales = GetSales(db).Where(s => s.Amount >= 10m).AsCte("big_sales");
        var count = bigSales.Count(s => s.Region == "east");

        Assert.Equal(2, count);
    }

    [IntegrationFact]
    public void BulkCopy_inserts_batch_in_single_statement()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var copied = GetSales(db).BulkCopy(
            new BulkCopyOptions { BulkCopyType = BulkCopyType.MultipleRows },
            new[]
            {
                new Sale { Id = 100, Region = "north", Amount = 1.00m },
                new Sale { Id = 101, Region = "north", Amount = 2.00m },
                new Sale { Id = 102, Region = "north", Amount = 3.00m },
            });

        Assert.Equal(3, copied.RowsCopied);
        Assert.Equal(3, GetSales(db).Count(s => s.Region == "north"));
    }

    [IntegrationFact]
    public void Merge_upserts_rows()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var affected = GetSales(db)
            .Merge()
            .Using(new[]
            {
                new Sale { Id = 1, Region = "east", Amount = 99.99m },  // update
                new Sale { Id = 200, Region = "south", Amount = 7.77m }, // insert
            })
            .OnTargetKey()
            .UpdateWhenMatched()
            .InsertWhenNotMatched()
            .Merge();

        Assert.Equal(2, affected);
        Assert.Equal(99.99m, GetSales(db).Single(s => s.Id == 1).Amount);
        Assert.Equal(7.77m, GetSales(db).Single(s => s.Id == 200).Amount);
    }
}
