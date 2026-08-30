namespace GlutenFree.Databricks.AdoNet.IntegrationTests;

/// <summary>
/// Resolves integration test configuration from environment variables, falling back to
/// User-scope variables on Windows (so freshly-set values are found without a new login).
/// </summary>
public static class IntegrationConfig
{
    public static string? Host => Get("DATABRICKS_HOST")?.TrimEnd('/');

    public static string? Token => Get("DATABRICKS_TOKEN");

    public static string? WarehouseId => Get("DATABRICKS_WAREHOUSE_ID");

    public static bool IsConfigured => Host is not null && Token is not null && WarehouseId is not null;

    public static string ConnectionString =>
        $"Host={Host};WarehouseId={WarehouseId};Token={Token}";

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int s_swept;

    /// <summary>
    /// Creates a schema name carrying a creation timestamp (hex unix seconds) so aborted
    /// runs can be swept by age: <c>adonet_&lt;hexSeconds&gt;_&lt;tag&gt;_&lt;guid&gt;</c>.
    /// </summary>
    public static string CreateSchemaName(string tag)
        => $"adonet_{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x}_{tag}_{Guid.NewGuid():N}";

    /// <summary>
    /// Drops leftover <c>adonet_*</c> schemas from previous aborted runs (older than two
    /// hours, or legacy names without a timestamp). Runs once per process; keeps killed
    /// test runs from leaking tables against the metastore quota. Note: Databricks retains
    /// dropped managed tables for ~7 days (UNDROP), and those still count toward the
    /// 500-tables-per-metastore quota until purged automatically.
    /// </summary>
    public static async Task SweepStaleSchemasAsync(DatabricksConnection connection)
    {
        if (Interlocked.Exchange(ref s_swept, 1) == 1)
        {
            return;
        }

        var stale = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText =
                "SELECT schema_name FROM workspace.information_schema.schemata WHERE schema_name LIKE 'adonet%'";
            await using var reader = await list.ExecuteReaderAsync();
            var cutoff = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var parts = name.Split('_');
                var hasTimestamp = parts.Length >= 3
                    && long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var created)
                    && created >= cutoff;
                if (!hasTimestamp)
                {
                    // Legacy names (no timestamp) or older than the cutoff: stale leftovers.
                    stale.Add(name);
                }
            }
        }

        foreach (var name in stale)
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS workspace.`{name}` CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>A fact that is skipped unless the DATABRICKS_* environment variables are set.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            Skip = "Set DATABRICKS_HOST, DATABRICKS_TOKEN and DATABRICKS_WAREHOUSE_ID to run integration tests "
                + "(see planning/integration-test-setup.md).";
        }

        Timeout = 300_000; // Warehouse cold start can take a while on the first test.
    }
}
