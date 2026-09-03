using GlutenFree.Databricks.AdoNet.IntegrationTests;

namespace GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests;

/// <summary>
/// Live coverage for interactive transactions (<c>BEGIN TRANSACTION</c> …
/// <c>COMMIT</c>/<c>ROLLBACK</c>), which are session state and therefore Thrift-only.
/// </summary>
/// <remarks>
/// Opt-in: set <c>DATABRICKS_TRANSACTIONS=1</c>. Databricks requires every table written to
/// in a transaction to be a Unity Catalog managed Delta/Iceberg table with
/// <c>delta.feature.catalogManaged</c> enabled, and the feature is not available on every
/// workspace tier — so these are gated rather than run by default.
/// See <see href="https://docs.databricks.com/aws/en/transactions/"/>.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ThriftTransactionIntegrationTests
{
    private const string Schema = "adodotnet_txn_v1";
    private const string Table = "txn_rows";
    private const string QualifiedTable = $"workspace.{Schema}.{Table}";

    private static async Task<DatabricksConnection> OpenPreparedConnectionAsync()
    {
        var connection = IntegrationConfig.CreateConnection();
        await connection.OpenAsync();
        await IntegrationConfig.EnsureVersionedSchemaAsync(
            connection,
            Schema,
            $"""
             CREATE TABLE IF NOT EXISTS {QualifiedTable} (
                 run_id STRING,
                 value STRING
             ) USING DELTA TBLPROPERTIES ('delta.feature.catalogManaged' = 'supported')
             """);
        return connection;
    }

    private static async Task<long> CountRowsAsync(DatabricksConnection connection, string runId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QualifiedTable} WHERE run_id = :run_id";
        command.Parameters.AddWithValue("run_id", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetInt64(0);
    }

    private static async Task InsertAsync(DatabricksConnection connection, string runId, string value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {QualifiedTable} (run_id, value) VALUES (:run_id, :value)";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }

    [TransactionIntegrationFact]
    public async Task Commit_persists_every_statement_in_the_transaction()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        try
        {
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await InsertAsync(connection, runId, "first");
                await InsertAsync(connection, runId, "second");
                await transaction.CommitAsync();
            }

            Assert.Equal(2L, await CountRowsAsync(connection, runId));
        }
        finally
        {
            await IntegrationConfig.DeleteRunRowsAsync(connection, Schema, runId, Table);
        }
    }

    [TransactionIntegrationFact]
    public async Task Rollback_discards_every_statement_in_the_transaction()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        try
        {
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await InsertAsync(connection, runId, "discarded");
                await transaction.RollbackAsync();
            }

            Assert.Equal(0L, await CountRowsAsync(connection, runId));
        }
        finally
        {
            await IntegrationConfig.DeleteRunRowsAsync(connection, Schema, runId, Table);
        }
    }

    [TransactionIntegrationFact]
    public async Task Disposing_without_committing_rolls_back()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        try
        {
            await using (await connection.BeginTransactionAsync())
            {
                await InsertAsync(connection, runId, "abandoned");
            }

            Assert.Null(connection.CurrentTransaction);
            Assert.Equal(0L, await CountRowsAsync(connection, runId));
        }
        finally
        {
            await IntegrationConfig.DeleteRunRowsAsync(connection, Schema, runId, Table);
        }
    }

    [TransactionIntegrationFact]
    public async Task Uncommitted_rows_are_invisible_to_another_connection()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        try
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await InsertAsync(connection, runId, "pending");

            await using (var observer = IntegrationConfig.CreateConnection())
            {
                await observer.OpenAsync();
                Assert.Equal(0L, await CountRowsAsync(observer, runId));
            }

            await transaction.CommitAsync();

            await using (var observer = IntegrationConfig.CreateConnection())
            {
                await observer.OpenAsync();
                Assert.Equal(1L, await CountRowsAsync(observer, runId));
            }
        }
        finally
        {
            await IntegrationConfig.DeleteRunRowsAsync(connection, Schema, runId, Table);
        }
    }

    private sealed class TransactionIntegrationFactAttribute : FactAttribute
    {
        public TransactionIntegrationFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!IntegrationConfig.IsConfigured)
            {
                Skip = "Set DATABRICKS_HOST, DATABRICKS_TOKEN and DATABRICKS_WAREHOUSE_ID to run integration tests.";
            }
            else if (Environment.GetEnvironmentVariable("DATABRICKS_TRANSACTIONS") != "1")
            {
                Skip = "Set DATABRICKS_TRANSACTIONS=1 to run transaction integration tests; they require a "
                    + "workspace where multi-statement transactions and catalog-managed tables are available.";
            }

            Timeout = 300_000;
        }
    }
}
