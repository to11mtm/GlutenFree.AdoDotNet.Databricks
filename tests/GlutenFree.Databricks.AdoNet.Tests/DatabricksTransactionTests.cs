using System.Data;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>
/// Covers <see cref="DatabricksTransaction"/> over a session-capable (Thrift-like) transport.
/// </summary>
public class DatabricksTransactionTests
{
    private static (DatabricksConnection Connection, FakeTransport Transport) CreateSessionConnection()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        transport.SupportsTransactions = true;
        transport.NextResponse = Responses.EmptySuccess;
        return (connection, transport);
    }

    [Fact]
    public async Task Begin_and_commit_emit_transaction_control_statements()
    {
        var (connection, transport) = CreateSessionConnection();
        await connection.OpenAsync();

        var transaction = connection.BeginTransaction();
        Assert.Same(transaction, connection.CurrentTransaction);
        Assert.Equal(["BEGIN TRANSACTION"], transport.ExecutedSql);

        transaction.Commit();
        Assert.Equal(["BEGIN TRANSACTION", "COMMIT"], transport.ExecutedSql);
        Assert.Null(connection.CurrentTransaction);
        Assert.True(((DatabricksTransaction)transaction).IsCompleted);
    }

    [Fact]
    public async Task Rollback_emits_rollback()
    {
        var (connection, transport) = CreateSessionConnection();
        await connection.OpenAsync();

        var transaction = await connection.BeginTransactionAsync();
        await transaction.RollbackAsync();

        Assert.Equal(["BEGIN TRANSACTION", "ROLLBACK"], transport.ExecutedSql);
        Assert.Null(connection.CurrentTransaction);
    }

    [Fact]
    public async Task Dispose_rolls_back_an_uncompleted_transaction()
    {
        var (connection, transport) = CreateSessionConnection();
        await connection.OpenAsync();

        using (connection.BeginTransaction())
        {
            // Falls out of scope without an explicit Commit.
        }

        Assert.Equal(["BEGIN TRANSACTION", "ROLLBACK"], transport.ExecutedSql);
        Assert.Null(connection.CurrentTransaction);
    }

    [Fact]
    public async Task Dispose_after_commit_does_not_roll_back()
    {
        var (connection, transport) = CreateSessionConnection();
        await connection.OpenAsync();

        using (var transaction = connection.BeginTransaction())
        {
            transaction.Commit();
        }

        Assert.Equal(["BEGIN TRANSACTION", "COMMIT"], transport.ExecutedSql);
    }

    [Fact]
    public async Task Completing_twice_throws()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();

        var transaction = connection.BeginTransaction();
        transaction.Commit();

        Assert.Throws<InvalidOperationException>(transaction.Commit);
        Assert.Throws<InvalidOperationException>(transaction.Rollback);
    }

    [Fact]
    public async Task Only_one_transaction_at_a_time()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        var ex = Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
        Assert.Contains("only one transaction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Isolation_level_is_reported_as_snapshot_and_the_request_is_retained()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();

        using var transaction = (DatabricksTransaction)connection.BeginTransaction(IsolationLevel.ReadCommitted);

        Assert.Equal(IsolationLevel.Snapshot, transaction.IsolationLevel);
        Assert.Equal(IsolationLevel.ReadCommitted, transaction.RequestedIsolationLevel);
    }

    [Fact]
    public async Task Savepoints_are_not_supported()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        Assert.False(transaction.SupportsSavepoints);
    }

    [Fact]
    public async Task Closing_the_connection_abandons_the_transaction_without_a_rollback_statement()
    {
        var (connection, transport) = CreateSessionConnection();
        await connection.OpenAsync();

        var transaction = connection.BeginTransaction();
        await connection.CloseAsync();

        // The session is gone, so uncommitted work is already discarded server-side.
        Assert.Equal(["BEGIN TRANSACTION"], transport.ExecutedSql);
        Assert.Null(connection.CurrentTransaction);
        transaction.Dispose();
        Assert.Equal(["BEGIN TRANSACTION"], transport.ExecutedSql);
    }

    [Fact]
    public async Task Changing_catalog_or_schema_inside_a_transaction_throws()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("other"));
        Assert.Throws<InvalidOperationException>(() => connection.ChangeCatalog("other"));
    }

    [Fact]
    public async Task Command_accepts_a_transaction_from_the_same_connection()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        Assert.Same(transaction, command.Transaction);
    }

    [Fact]
    public async Task Command_rejects_a_transaction_from_another_connection()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();
        var (other, _) = CreateSessionConnection();
        await other.OpenAsync();

        using var transaction = other.BeginTransaction();
        using var command = connection.CreateCommand();

        Assert.Throws<InvalidOperationException>(() => command.Transaction = transaction);
    }

    [Fact]
    public async Task Command_rejects_a_completed_transaction()
    {
        var (connection, _) = CreateSessionConnection();
        await connection.OpenAsync();

        var transaction = connection.BeginTransaction();
        transaction.Commit();

        using var command = connection.CreateCommand();
        Assert.Throws<InvalidOperationException>(() => command.Transaction = transaction);
    }
}
