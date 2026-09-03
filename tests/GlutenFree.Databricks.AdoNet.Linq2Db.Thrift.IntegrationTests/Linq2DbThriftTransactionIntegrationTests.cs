using GlutenFree.Databricks.AdoNet.IntegrationTests;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Thrift.IntegrationTests;

/// <summary>
/// Live coverage for linq2db interactive transactions over the Thrift transport, via the
/// <see cref="DatabricksThriftTools"/> provider flavor (<c>TransactionsSupported=true</c>).
/// Reuses the fixed <c>adodotnet_txn_v1.txn_rows</c> table from the ADO.NET Thrift
/// transaction suite to keep the metastore managed-table count constant.
/// </summary>
/// <remarks>
/// Opt-in: set <c>DATABRICKS_TRANSACTIONS=1</c>. Databricks requires every table written to
/// in a transaction to be a Unity Catalog managed Delta/Iceberg table with
/// <c>delta.feature.catalogManaged</c> enabled, and the feature is not available on every
/// workspace tier — so these are gated rather than run by default.
/// See <see href="https://docs.databricks.com/aws/en/transactions/"/>.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class Linq2DbThriftTransactionIntegrationTests
{
    private const string Catalog = "workspace";
    private const string Schema = "adodotnet_txn_v1";
    private const string Table = "txn_rows";
    private const string QualifiedTable = $"{Catalog}.{Schema}.{Table}";

    private sealed class TxnRow
    {
        [Column("run_id")] public string RunId { get; set; } = "";

        [Column("value")] public string? Value { get; set; }
    }

    private static ITable<TxnRow> RowsTable(DataConnection db)
        => db.GetTable<TxnRow>()
            .TableName(Table)
            .SchemaName(Schema)
            .ServerName(Catalog);

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

    private static int CountRows(DataConnection db, string runId)
        => RowsTable(db).Count(r => r.RunId == runId);

    [IntegrationFact]
    public void Turnkey_connection_string_flow_queries_over_thrift()
    {
        using var db = DatabricksThriftTools.CreateDataConnection(IntegrationConfig.ConnectionString);

        Assert.Equal(1, db.Select(() => 1));
    }

    [TransactionIntegrationFact]
    public async Task Commit_persists_linq2db_writes()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        using var db = DatabricksThriftTools.CreateDataConnection(connection);
        try
        {
            using (var tx = db.BeginTransaction())
            {
                RowsTable(db).Insert(() => new TxnRow { RunId = runId, Value = "first" });
                RowsTable(db).Insert(() => new TxnRow { RunId = runId, Value = "second" });
                tx.Commit();
            }

            Assert.Equal(2, CountRows(db, runId));
        }
        finally
        {
            await IntegrationConfig.DeleteRunRowsAsync(connection, Schema, runId, Table);
        }
    }

    [TransactionIntegrationFact]
    public async Task Rollback_discards_linq2db_writes()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        using var db = DatabricksThriftTools.CreateDataConnection(connection);
        try
        {
            using (var tx = db.BeginTransaction())
            {
                RowsTable(db).Insert(() => new TxnRow { RunId = runId, Value = "discarded" });
                tx.Rollback();
            }

            Assert.Equal(0, CountRows(db, runId));
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
        using var db = DatabricksThriftTools.CreateDataConnection(connection);
        try
        {
            using (db.BeginTransaction())
            {
                RowsTable(db).Insert(() => new TxnRow { RunId = runId, Value = "abandoned" });
            }

            Assert.Null(connection.CurrentTransaction);
            Assert.Equal(0, CountRows(db, runId));
        }
        finally
        {
            await IntegrationConfig.DeleteRunRowsAsync(connection, Schema, runId, Table);
        }
    }

    [TransactionIntegrationFact]
    public async Task Sequential_transactions_work_on_one_data_connection()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        using var db = DatabricksThriftTools.CreateDataConnection(connection);
        try
        {
            using (var tx = db.BeginTransaction())
            {
                RowsTable(db).Insert(() => new TxnRow { RunId = runId, Value = "kept" });
                tx.Commit();
            }

            using (var tx = db.BeginTransaction())
            {
                RowsTable(db).Insert(() => new TxnRow { RunId = runId, Value = "discarded" });
                tx.Rollback();
            }

            Assert.Equal(1, CountRows(db, runId));
        }
        finally
        {
            await IntegrationConfig.DeleteRunRowsAsync(connection, Schema, runId, Table);
        }
    }

    [TransactionIntegrationFact]
    public async Task BulkCopy_enlists_in_the_ambient_transaction()
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenPreparedConnectionAsync();
        using var db = DatabricksThriftTools.CreateDataConnection(connection);
        try
        {
            var rows = Enumerable.Range(1, 5)
                .Select(i => new TxnRow { RunId = runId, Value = $"row-{i}" })
                .ToList();

            using (var tx = db.BeginTransaction())
            {
                RowsTable(db).BulkCopy(rows);
                tx.Rollback();
            }

            Assert.Equal(0, CountRows(db, runId));

            using (var tx = db.BeginTransaction())
            {
                RowsTable(db).BulkCopy(rows);
                tx.Commit();
            }

            Assert.Equal(5, CountRows(db, runId));
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
