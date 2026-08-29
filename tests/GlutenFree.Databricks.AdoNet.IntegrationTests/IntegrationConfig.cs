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
