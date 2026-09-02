using GlutenFree.Databricks.AdoNet.Linq2Db.Internal;
using GlutenFree.Databricks.AdoNet.Tests;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Thrift.Tests;

/// <summary>
/// Verifies the Thrift-flavored linq2db provider drives real interactive transactions,
/// using the in-memory transport with <see cref="FakeTransport.SupportsTransactions"/> set.
/// </summary>
public class DatabricksThriftToolsTests
{
    [Table("orders")]
    private sealed class Order
    {
        [Column("id")] public long Id { get; set; }

        [Column("amount")] public decimal Amount { get; set; }
    }

    private static (DataConnection Db, FakeTransport Transport) CreateDb(bool supportsTransactions = true)
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        transport.SupportsTransactions = supportsTransactions;
        transport.NextResponse = Responses.EmptySuccess;
        var db = DatabricksThriftTools.CreateDataConnection(connection);
        return (db, transport);
    }

    [Fact]
    public void Provider_flavors_differ_only_in_name_and_transaction_support()
    {
        var rest = (DatabricksDataProvider)DatabricksTools.GetDataProvider();
        var thrift = (DatabricksDataProvider)DatabricksThriftTools.GetDataProvider();

        Assert.Equal(DatabricksProviderName.Databricks, rest.Name);
        Assert.Equal(DatabricksProviderName.DatabricksThrift, thrift.Name);
        Assert.False(rest.TransactionsSupported);
        Assert.True(thrift.TransactionsSupported);
    }

    [Fact]
    public void BeginTransaction_and_commit_execute_control_statements()
    {
        var (db, transport) = CreateDb();
        using var _ = db;

        using var tx = db.BeginTransaction();
        tx.Commit();

        Assert.Equal(["BEGIN TRANSACTION", "COMMIT"], transport.ExecutedSql);
    }

    [Fact]
    public void BeginTransaction_and_rollback_execute_control_statements()
    {
        var (db, transport) = CreateDb();
        using var _ = db;

        using var tx = db.BeginTransaction();
        tx.Rollback();

        Assert.Equal(["BEGIN TRANSACTION", "ROLLBACK"], transport.ExecutedSql);
    }

    [Fact]
    public void Disposing_without_committing_rolls_back()
    {
        var (db, transport) = CreateDb();
        using var _ = db;

        using (db.BeginTransaction())
        {
        }

        Assert.Equal(["BEGIN TRANSACTION", "ROLLBACK"], transport.ExecutedSql);
    }

    [Fact]
    public void Statements_run_between_begin_and_commit_on_the_same_session()
    {
        var (db, transport) = CreateDb();
        using var _ = db;

        using var tx = db.BeginTransaction();
        db.GetTable<Order>()
            .Where(o => o.Id == 7L)
            .Set(o => o.Amount, 1.23m)
            .Update();
        tx.Commit();

        Assert.Collection(
            transport.ExecutedSql,
            sql => Assert.Equal("BEGIN TRANSACTION", sql),
            sql => Assert.StartsWith(
                "UPDATE `orders`",
                string.Join(' ', sql.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))),
            sql => Assert.Equal("COMMIT", sql));
    }

    [Fact]
    public async Task Async_transaction_lifecycle_executes_control_statements()
    {
        var (db, transport) = CreateDb();
        using var _ = db;

        await using (var tx = await db.BeginTransactionAsync())
        {
            await tx.CommitAsync();
        }

        await using (var tx = await db.BeginTransactionAsync())
        {
            await tx.RollbackAsync();
        }

        Assert.Equal(
            ["BEGIN TRANSACTION", "COMMIT", "BEGIN TRANSACTION", "ROLLBACK"],
            transport.ExecutedSql);
    }

    [Fact]
    public void BeginTransaction_on_non_session_transport_throws()
    {
        var (db, _) = CreateDb(supportsTransactions: false);
        using var _1 = db;

        var ex = Assert.Throws<NotSupportedException>(() => db.BeginTransaction());
        Assert.Contains("UseThriftTransport", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDataConnection_leaves_preconfigured_transport_untouched()
    {
        var (connection, _) = DatabricksConnectionTests.CreateOpenable();
        var factory = connection.TransportFactory;

        using var db = DatabricksThriftTools.CreateDataConnection(connection);

        Assert.Same(factory, connection.TransportFactory);
    }

    [Fact]
    public void CreateDataConnection_opts_unconfigured_connection_into_thrift()
    {
        using var connection = new DatabricksConnection(
            "Host=https://example.databricks.net;WarehouseId=w;Token=t");
        Assert.Null(connection.TransportFactory);

        using var db = DatabricksThriftTools.CreateDataConnection(connection);

        Assert.NotNull(connection.TransportFactory);
    }

    [Fact]
    public void Provider_created_connection_from_connection_string_uses_thrift()
    {
        var provider = DatabricksThriftTools.GetDataProvider();

        using var connection = (DatabricksConnection)provider.CreateConnection(
            "Host=https://example.databricks.net;WarehouseId=w;Token=t");

        Assert.NotNull(connection.TransportFactory);
    }

    [Fact]
    public void Rest_provider_created_connection_has_no_transport_factory()
    {
        var provider = DatabricksTools.GetDataProvider();

        using var connection = (DatabricksConnection)provider.CreateConnection(
            "Host=https://example.databricks.net;WarehouseId=w;Token=t");

        Assert.Null(connection.TransportFactory);
    }
}
