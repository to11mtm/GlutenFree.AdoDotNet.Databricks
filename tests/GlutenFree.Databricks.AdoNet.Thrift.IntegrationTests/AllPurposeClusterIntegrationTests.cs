using GlutenFree.Databricks.AdoNet.IntegrationTests;

namespace GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests;

/// <summary>
/// Live coverage for all-purpose (interactive) cluster endpoints, which only speak the
/// Thrift protocol. Requires a running cluster: set <c>DATABRICKS_CLUSTER_HTTP_PATH</c>
/// (e.g. <c>/sql/protocolv1/o/1234567890/0830-123456-abcdef12</c>) alongside the usual
/// host/token variables. Skipped otherwise — Free Edition workspaces have no all-purpose
/// clusters, so this typically only runs against paid workspaces.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AllPurposeClusterIntegrationTests
{
    private static string? ClusterHttpPath =>
        Environment.GetEnvironmentVariable("DATABRICKS_CLUSTER_HTTP_PATH") is { Length: > 0 } v ? v : null;

    private static DatabricksConnection CreateClusterConnection()
        => new DatabricksConnection(
                $"Host={IntegrationConfig.Host};HttpPath={ClusterHttpPath};Token={IntegrationConfig.Token}")
            .UseThriftTransport();

    [ClusterIntegrationFact]
    public async Task Select_one_roundtrips_on_cluster()
    {
        await using var connection = CreateClusterConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 AS one";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
    }

    [ClusterIntegrationFact]
    public async Task Parameters_bind_on_cluster()
    {
        await using var connection = CreateClusterConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT :num AS n, :text AS t";
        command.Parameters.AddWithValue("num", 42L);
        command.Parameters.AddWithValue("text", "it's safe; -- no injection");

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal("it's safe; -- no injection", reader.GetString(1));
    }

    private sealed class ClusterIntegrationFactAttribute : FactAttribute
    {
        public ClusterIntegrationFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (IntegrationConfig.Host is null || IntegrationConfig.Token is null)
            {
                Skip = "Set DATABRICKS_HOST and DATABRICKS_TOKEN to run integration tests.";
            }
            else if (ClusterHttpPath is null)
            {
                Skip = "Set DATABRICKS_CLUSTER_HTTP_PATH to a running all-purpose cluster's HTTP path "
                    + "to run cluster integration tests.";
            }

            Timeout = 600_000; // Cluster auto-start can take minutes.
        }
    }
}
