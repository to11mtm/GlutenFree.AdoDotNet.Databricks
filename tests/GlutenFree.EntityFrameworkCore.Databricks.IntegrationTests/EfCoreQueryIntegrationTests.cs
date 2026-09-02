using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>A row in the integration test table.</summary>
public class Widget
{
    public string RunId { get; set; } = string.Empty;

    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public bool Active { get; set; }
}

/// <summary>Context mapping <see cref="Widget" /> onto the shared integration test table.</summary>
public class WidgetContext(DbContextOptions<WidgetContext> options) : DbContext(options)
{
    public const string Schema = "adodotnet_ef_v1";
    public const string Table = "ef_widgets";

    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Widget>(b =>
        {
            b.ToTable(Table, Schema);
            b.HasKey(w => w.Id);
            // Databricks cannot return store-generated values, so keys are client-generated.
            b.Property(w => w.Id).ValueGeneratedNever();
            b.Property(w => w.RunId).HasColumnName("run_id");
            b.Property(w => w.Id).HasColumnName("id");
            b.Property(w => w.Name).HasColumnName("name");
            b.Property(w => w.Price).HasColumnName("price").HasColumnType("DECIMAL(18, 2)");
            b.Property(w => w.Active).HasColumnName("active");
        });
}

/// <summary>
/// Live coverage for the EF Core provider against a Databricks SQL warehouse: proves the
/// generated SQL is actually accepted and that results materialize.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreQueryIntegrationTests : IAsyncLifetime
{
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private DatabricksConnection? _connection;

    private static string QualifiedTable => $"workspace.{WidgetContext.Schema}.{WidgetContext.Table}";

    public async ValueTask InitializeAsync()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            return;
        }

        _connection = IntegrationConfig.CreateConnection("Catalog=workspace;Schema=" + WidgetContext.Schema);
        await _connection.OpenAsync();
        await IntegrationConfig.EnsureVersionedSchemaAsync(
            _connection,
            WidgetContext.Schema,
            $"""
             CREATE TABLE IF NOT EXISTS {QualifiedTable} (
                 run_id STRING,
                 id BIGINT,
                 name STRING,
                 price DECIMAL(18, 2),
                 active BOOLEAN
             ) USING DELTA
             """);

        await using var seed = _connection.CreateCommand();
        seed.CommandText =
            $"INSERT INTO {QualifiedTable} VALUES "
            + "(:run_id, 1, 'alpha', 10.50, true), "
            + "(:run_id, 2, 'beta', 20.00, false), "
            + "(:run_id, 3, 'gamma', 30.25, true)";
        seed.Parameters.AddWithValue("run_id", _runId);
        await seed.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await IntegrationConfig.DeleteRunRowsAsync(
                _connection, WidgetContext.Schema, _runId, WidgetContext.Table);
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private WidgetContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseDatabricks(IntegrationConfig.ConnectionString, o => o.UseCatalog("workspace"))
            .Options;

        return new WidgetContext(options);
    }

    [IntegrationFact]
    public async Task Select_materializes_rows()
    {
        await using var context = CreateContext();

        var widgets = await context.Widgets
            .Where(w => w.RunId == _runId)
            .OrderBy(w => w.Id)
            .ToListAsync();

        Assert.Equal(3, widgets.Count);
        Assert.Equal("alpha", widgets[0].Name);
        Assert.Equal(10.50m, widgets[0].Price);
        Assert.True(widgets[0].Active);
        Assert.False(widgets[1].Active);
    }

    [IntegrationFact]
    public async Task Where_with_parameters_filters_server_side()
    {
        await using var context = CreateContext();

        var names = await context.Widgets
            .Where(w => w.RunId == _runId && w.Price > 15m && w.Active)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["gamma"], names);
    }

    [IntegrationFact]
    public async Task Skip_and_take_paginate()
    {
        await using var context = CreateContext();

        var page = await context.Widgets
            .Where(w => w.RunId == _runId)
            .OrderBy(w => w.Id)
            .Skip(1)
            .Take(1)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta"], page);
    }

    [IntegrationFact]
    public async Task Skip_without_take_uses_the_maximum_limit()
    {
        await using var context = CreateContext();

        var rest = await context.Widgets
            .Where(w => w.RunId == _runId)
            .OrderBy(w => w.Id)
            .Skip(1)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta", "gamma"], rest);
    }

    [IntegrationFact]
    public async Task Aggregates_translate()
    {
        await using var context = CreateContext();

        var query = context.Widgets.Where(w => w.RunId == _runId);

        Assert.Equal(3, await query.CountAsync());
        Assert.Equal(60.75m, await query.SumAsync(w => w.Price));
        Assert.Equal(30.25m, await query.MaxAsync(w => w.Price));
    }

    [IntegrationFact]
    public async Task String_translations_run_server_side()
    {
        await using var context = CreateContext();

        var names = await context.Widgets
            .Where(w => w.RunId == _runId && w.Name.StartsWith("g"))
            .Select(w => w.Name.ToUpper())
            .ToListAsync();

        Assert.Equal(["GAMMA"], names);
    }
}
