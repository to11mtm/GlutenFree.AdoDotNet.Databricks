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

    public DateTime CreatedAt { get; set; }
}

/// <summary>Context mapping <see cref="Widget" /> onto the shared integration test table.</summary>
public class WidgetContext(DbContextOptions<WidgetContext> options) : DbContext(options)
{
    // Bump the version suffix rather than altering the table if its shape has to change.
    public const string Schema = "adodotnet_ef_v2";
    public const string Table = "ef_widgets";

    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Widget>(b =>
        {
            b.ToTable(Table, Schema);
            b.HasKey(w => w.Id);
            // Databricks cannot return store-generated values, so keys are client-generated.
            b.Property(w => w.Id).ValueGeneratedNever().HasColumnName("id");
            b.Property(w => w.RunId).HasColumnName("run_id");
            b.Property(w => w.Name).HasColumnName("name");
            b.Property(w => w.Price).HasColumnName("price").HasColumnType("DECIMAL(18, 2)");
            b.Property(w => w.Active).HasColumnName("active");
            b.Property(w => w.CreatedAt).HasColumnName("created_at");
        });
}

/// <summary>
/// Creates the shared widget table and seeds three rows scoped to this run, so the EF suites can
/// query a known data set. Rows are deleted on teardown; the table itself is never dropped
/// (see <see cref="IntegrationConfig.EnsureVersionedSchemaAsync" />).
/// </summary>
[Trait("Category", "Integration")]
public abstract class WidgetFixture : IAsyncLifetime
{
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private DatabricksConnection? _connection;

    private static string QualifiedTable => $"workspace.{WidgetContext.Schema}.{WidgetContext.Table}";

    /// <summary>
    /// Creates a context pointed at the test warehouse. The connection comes from
    /// <see cref="IntegrationConfig.CreateConnection" /> so that a wrapping test project can
    /// re-run the whole suite over another transport via
    /// <see cref="IntegrationConfig.ConnectionCustomizer" />. Catalog/schema are set on the
    /// connection string rather than through <c>UseCatalog</c>/<c>UseSchema</c>, which only
    /// apply when the provider constructs the connection itself.
    /// </summary>
    protected static WidgetContext CreateContext()
    {
        var connection = IntegrationConfig.CreateConnection(
            "Catalog=workspace;Schema=" + WidgetContext.Schema);

        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseDatabricks(connection, contextOwnsConnection: true)
            .Options;

        return new WidgetContext(options);
    }

    /// <summary>This run's rows only, so parallel runs cannot see each other's data.</summary>
    protected IQueryable<Widget> Widgets(WidgetContext context)
        => context.Widgets.Where(w => w.RunId == _runId);

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
                 active BOOLEAN,
                 created_at TIMESTAMP_NTZ
             ) USING DELTA
             """);

        await using var seed = _connection.CreateCommand();
        seed.CommandText =
            $"INSERT INTO {QualifiedTable} VALUES "
            + "(:run_id, 1, 'alpha', 10.50, true,  TIMESTAMP_NTZ'2026-03-01 08:00:00'), "
            + "(:run_id, 2, 'beta',  20.00, false, TIMESTAMP_NTZ'2026-03-01 09:00:00'), "
            + "(:run_id, 3, 'gamma', 30.25, true,  TIMESTAMP_NTZ'2026-03-01 10:00:00')";
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
}
