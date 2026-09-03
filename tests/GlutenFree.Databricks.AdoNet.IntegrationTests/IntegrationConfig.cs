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

    /// <summary>
    /// Hook applied to every connection created by <see cref="CreateConnection"/>. The
    /// Thrift integration test project sets this (via a module initializer) to opt its
    /// entire run into the Thrift transport; this base project runs plain REST.
    /// </summary>
    public static Action<DatabricksConnection>? ConnectionCustomizer { get; set; }

    public static bool IsConfigured => Host is not null && Token is not null && WarehouseId is not null;

    public static string ConnectionString =>
        $"Host={Host};WarehouseId={WarehouseId};Token={Token}";

    /// <summary>
    /// Creates a test connection, applying <see cref="ConnectionCustomizer"/> so the whole
    /// shared suite can be re-run against another transport by a wrapping test project.
    /// </summary>
    public static DatabricksConnection CreateConnection(string? extraSettings = null)
    {
        var connection = new DatabricksConnection(
            extraSettings is null ? ConnectionString : ConnectionString + ";" + extraSettings);
        ConnectionCustomizer?.Invoke(connection);
        return connection;
    }

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
    /// Ensures a fixed, versioned test schema and its tables exist (idempotent
    /// <c>IF NOT EXISTS</c> DDL). Schemas/tables are never dropped — tests scope their rows
    /// with a per-run <c>run_id</c> column and delete only those rows on cleanup, keeping the
    /// metastore table count constant (dropped managed tables would count against the
    /// 500-per-metastore quota for ~7 days due to UNDROP retention). If a table's shape must
    /// change, bump the schema version suffix (v1 → v2) instead of altering it.
    /// </summary>
    public static async Task EnsureVersionedSchemaAsync(
        DatabricksConnection connection, string schema, params string[] createTableStatements)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA IF NOT EXISTS workspace.{schema}";
            await create.ExecuteNonQueryAsync();
        }

        foreach (var statement in createTableStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Deletes this run's rows (matched by <c>run_id</c>) from the given tables.</summary>
    public static async Task DeleteRunRowsAsync(
        DatabricksConnection connection, string schema, string runId, params string[] tables)
    {
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM workspace.{schema}.{table} WHERE run_id = :run_id";
            command.Parameters.AddWithValue("run_id", runId);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Drops legacy throwaway <c>adonet_&lt;name&gt;_&lt;hex run token&gt;</c> schemas left behind
    /// by the old per-run schema pattern (aborted runs skipped cleanup). Destructive, so it is
    /// double-gated: it only runs when <c>DATABRICKS_SWEEP_LEGACY_SCHEMAS=1</c> is explicitly
    /// set, and only drops schemas matching the exact legacy per-run shape (never fixed
    /// <c>adodotnet_*</c> schemas or other <c>adonet_</c>-prefixed names in a shared workspace).
    /// Runs once per process.
    /// </summary>
    public static async Task SweepStaleSchemasAsync(DatabricksConnection connection)
    {
        if (Get("DATABRICKS_SWEEP_LEGACY_SCHEMAS") != "1")
        {
            return;
        }

        if (Interlocked.Exchange(ref s_swept, 1) == 1)
        {
            return;
        }

        var stale = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText =
                "SELECT schema_name FROM workspace.information_schema.schemata WHERE schema_name LIKE 'adonet\\_%'";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                // Legacy per-run schemas were adonet_<tag>_<guid:N> or
                // adonet_<hexSeconds>_<tag>_<guid:N>; the trailing 32-hex GUID is required.
                if (System.Text.RegularExpressions.Regex.IsMatch(name, "^adonet_[a-z0-9_]+_[0-9a-f]{32}$"))
                {
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
    public IntegrationFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IntegrationConfig.IsConfigured)
        {
            Skip = "Set DATABRICKS_HOST, DATABRICKS_TOKEN and DATABRICKS_WAREHOUSE_ID to run integration tests "
                + "(see planning/integration-test-setup.md).";
        }

        Timeout = 300_000; // Warehouse cold start can take a while on the first test.
    }
}
