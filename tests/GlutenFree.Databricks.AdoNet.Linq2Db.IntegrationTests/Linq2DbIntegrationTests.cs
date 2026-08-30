using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.Databricks.AdoNet.Linq2Db;
using LinqToDB;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.IntegrationTests;

/// <summary>
/// End-to-end linq2db tests against a live Databricks SQL warehouse.
/// Skipped unless the DATABRICKS_* environment variables are set.
/// Uses a fixed versioned schema; rows are scoped by a per-run <c>run_id</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Linq2DbIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private const string Schema = "adodotnet_l2db_v1";
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private DatabricksConnection _connection = null!;

    private sealed class Product
    {
        [Column("run_id"), PrimaryKey(0)] public string RunId { get; set; } = "";

        [Column("id"), PrimaryKey(1)] public long Id { get; set; }

        [Column("name")] public string? Name { get; set; }

        [Column("price")] public decimal Price { get; set; }
    }

    private ITable<Product> ProductsTable(LinqToDB.Data.DataConnection db)
        => db.GetTable<Product>()
            .TableName("products")
            .SchemaName(Schema)
            .ServerName(Catalog);

    private IQueryable<Product> GetProducts(LinqToDB.Data.DataConnection db)
        => ProductsTable(db).Where(p => p.RunId == _runId);

    public async Task InitializeAsync()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            return;
        }

        _connection = new DatabricksConnection(IntegrationConfig.ConnectionString);
        await _connection.OpenAsync();
        await IntegrationConfig.SweepStaleSchemasAsync(_connection);
        await IntegrationConfig.EnsureVersionedSchemaAsync(
            _connection,
            Schema,
            $"CREATE TABLE IF NOT EXISTS {Catalog}.{Schema}.products " +
            "(run_id STRING, id BIGINT, name STRING, price DECIMAL(10,2))");
        await ExecuteAsync(
            $"INSERT INTO {Catalog}.{Schema}.products VALUES " +
            $"('{_runId}', 1, 'widget', 9.99), ('{_runId}', 2, 'gadget', 24.50), ('{_runId}', 3, 'gizmo', 100.00)");
    }

    public async Task DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await IntegrationConfig.DeleteRunRowsAsync(_connection, Schema, _runId, "products");
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
    public void Linq_where_orderby_select_roundtrips()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var expensive = GetProducts(db)
            .Where(p => p.Price > 10m)
            .OrderByDescending(p => p.Price)
            .Select(p => new { p.Name, p.Price })
            .ToList();

        Assert.Equal(2, expensive.Count);
        Assert.Equal("gizmo", expensive[0].Name);
        Assert.Equal(100.00m, expensive[0].Price);
        Assert.Equal("gadget", expensive[1].Name);
    }

    [IntegrationFact]
    public void Linq_parameterized_query_binds_values()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var name = "widget";
        var widget = GetProducts(db).Single(p => p.Name == name);

        Assert.Equal(1L, widget.Id);
        Assert.Equal(9.99m, widget.Price);
    }

    [IntegrationFact]
    public void Linq_insert_and_count_work()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        ProductsTable(db).Insert(() => new Product { RunId = _runId, Id = 4, Name = "doohickey", Price = 1.25m });

        Assert.Equal(4, GetProducts(db).Count());
        Assert.Equal(1.25m, GetProducts(db).Single(p => p.Id == 4L).Price);
    }

    [IntegrationFact]
    public void Linq_take_skip_paginate()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var page = GetProducts(db).OrderBy(p => p.Id).Skip(1).Take(1).ToList();

        var product = Assert.Single(page);
        Assert.Equal(2L, product.Id);
    }

    [IntegrationFact]
    public void Linq_aggregate_functions_work()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var total = GetProducts(db).Sum(p => p.Price);
        var max = GetProducts(db).Max(p => p.Price);

        Assert.Equal(134.49m, total);
        Assert.Equal(100.00m, max);
    }
}
