using System.Data;
using System.Data.Common;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// An interactive Databricks transaction: <c>BEGIN TRANSACTION</c> … <c>COMMIT</c>/<c>ROLLBACK</c>
/// issued against the connection's server-side session.
/// </summary>
/// <remarks>
/// <para>
/// Only transports that maintain a session support this (see
/// <see cref="Transport.IDatabricksTransport.SupportsTransactions"/>): transaction state lives
/// in the session, so the stateless REST transport cannot participate. Use the Thrift transport,
/// or submit a self-contained <c>BEGIN ATOMIC ... END;</c> block as a single statement.
/// </para>
/// <para>
/// Databricks requirements and limitations that apply here: every table written to must be a
/// Unity Catalog managed Delta or Iceberg table with catalog commits enabled; DDL and metadata
/// operations are not supported inside an interactive transaction; only one transaction may be
/// active per connection; conflicts are detected optimistically at commit time (interactive
/// transactions conflict at table granularity), so callers should be prepared to retry.
/// Savepoints are not supported.
/// </para>
/// </remarks>
public sealed class DatabricksTransaction : DbTransaction
{
    private DatabricksConnection? _connection;
    private bool _completed;

    internal DatabricksTransaction(DatabricksConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        RequestedIsolationLevel = isolationLevel;
    }

    /// <summary>
    /// The isolation level Databricks provides: a consistent snapshot is captured per table at
    /// first access, giving repeatable reads for the life of the transaction.
    /// </summary>
    /// <remarks>
    /// Databricks has a single isolation level, so the level requested at
    /// <c>BeginTransaction</c> time — available as <see cref="RequestedIsolationLevel"/> — does
    /// not change server behavior. Snapshot isolation is at least as strong as every level
    /// weaker than it, so a weaker request is satisfied rather than rejected.
    /// </remarks>
    public override IsolationLevel IsolationLevel => IsolationLevel.Snapshot;

    /// <summary>The isolation level the caller asked for; retained for diagnostics only.</summary>
    public IsolationLevel RequestedIsolationLevel { get; }

    /// <inheritdoc />
    protected override DbConnection? DbConnection => _connection;

    /// <summary>True once the transaction has been committed or rolled back.</summary>
    public bool IsCompleted => _completed;

    /// <inheritdoc />
    /// <remarks>Databricks does not support savepoints.</remarks>
    public override bool SupportsSavepoints => false;

    /// <inheritdoc />
    /// <remarks>Genuinely synchronous; prefer <see cref="CommitAsync"/>.</remarks>
    public override void Commit() => Complete(DatabricksTransactionSql.Commit);

    /// <inheritdoc />
    public override Task CommitAsync(CancellationToken cancellationToken = default)
        => CompleteAsync(DatabricksTransactionSql.Commit, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Genuinely synchronous; prefer <see cref="RollbackAsync"/>.</remarks>
    public override void Rollback() => Complete(DatabricksTransactionSql.Rollback);

    /// <inheritdoc />
    public override Task RollbackAsync(CancellationToken cancellationToken = default)
        => CompleteAsync(DatabricksTransactionSql.Rollback, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Rolls back an uncompleted transaction, matching the ADO.NET contract. Failures during that
    /// implicit rollback are swallowed: <see cref="IDisposable.Dispose"/> must not throw over an
    /// exception that is already unwinding, and the session's transaction state is cleared by the
    /// server when the session ends regardless.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && _connection is not null)
        {
            try
            {
                Rollback();
            }
            catch (DatabricksException)
            {
                // Best effort; see remarks.
            }
            catch (InvalidOperationException)
            {
                // The connection was closed underneath us; nothing to roll back.
            }
            finally
            {
                Detach();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="Dispose(bool)"/> for the implicit-rollback semantics.</remarks>
    public override async ValueTask DisposeAsync()
    {
        if (!_completed && _connection is not null)
        {
            try
            {
                await RollbackAsync().ConfigureAwait(false);
            }
            catch (DatabricksException)
            {
                // Best effort; see remarks.
            }
            catch (InvalidOperationException)
            {
                // The connection was closed underneath us; nothing to roll back.
            }
            finally
            {
                Detach();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the transaction completed without talking to the server. Used when the connection
    /// closes, which ends the session and therefore the transaction.
    /// </summary>
    internal void MarkAbandoned()
    {
        _completed = true;
        _connection = null;
    }

    private void Complete(string sql)
    {
        var connection = EnsureActive();
        connection.ExecuteControlStatement(sql);
        Detach();
    }

    private async Task CompleteAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = EnsureActive();
        await connection.ExecuteControlStatementAsync(sql, cancellationToken).ConfigureAwait(false);
        Detach();
    }

    private DatabricksConnection EnsureActive()
    {
        if (_completed || _connection is null)
        {
            throw new InvalidOperationException(
                "The transaction has already been committed or rolled back.");
        }

        _connection.EnsureOpen();
        return _connection;
    }

    private void Detach()
    {
        _completed = true;
        _connection?.ClearTransaction(this);
        _connection = null;
    }
}

/// <summary>The SQL Databricks uses to drive interactive transactions.</summary>
internal static class DatabricksTransactionSql
{
    internal const string Begin = "BEGIN TRANSACTION";
    internal const string Commit = "COMMIT";
    internal const string Rollback = "ROLLBACK";
}
