using System.Data;
using System.Data.Common;
using GlutenFree.Databricks.AdoNet.Auth;
using GlutenFree.Databricks.AdoNet.Transport;
using Microsoft.Extensions.Logging;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// An ADO.NET connection to a Databricks SQL warehouse.
/// </summary>
/// <remarks>
/// The default REST transport is stateless HTTP: <see cref="Open()"/> validates
/// configuration and prepares the transport rather than establishing a socket session.
/// <see cref="BeginDbTransaction"/> therefore requires a session-capable transport
/// (Thrift); see <see cref="DatabricksTransaction"/>.
/// </remarks>
public sealed class DatabricksConnection : DbConnection
{
    private DatabricksConnectionStringBuilder _builder = new();
    private IDatabricksTransport? _transport;
    private IDatabricksAuthenticator? _authenticator;
    private ConnectionState _state = ConnectionState.Closed;
    private DatabricksTransaction? _transaction;
    private string _catalog = string.Empty;
    private string _schema = string.Empty;

    // Test hooks: allow injecting a transport/authenticator without network access.
    internal Func<DatabricksConnection, IDatabricksTransport>? TransportFactory { get; set; }

    /// <summary>Creates a closed connection with no connection string.</summary>
    public DatabricksConnection()
    {
    }

    /// <summary>Creates a closed connection with the given connection string.</summary>
    public DatabricksConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>
    /// Optional logger factory used for provider diagnostics. Must be set before <see cref="Open()"/>.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _builder.ConnectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("The connection string cannot be changed while the connection is open.");
            }

            _builder = new DatabricksConnectionStringBuilder(value);
        }
    }

    /// <inheritdoc />
    public override string Database => _schema.Length > 0 ? _schema : _builder.Schema;

    /// <summary>The current default catalog for statements on this connection.</summary>
    public string Catalog => _catalog.Length > 0 ? _catalog : _builder.Catalog;

    /// <inheritdoc />
    public override string DataSource => _builder.Host;

    /// <inheritdoc />
    public override string ServerVersion => "Databricks SQL (Statement Execution API 2.0)";

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    /// <remarks>Reflects the <c>ConnectTimeout</c> connection-string value enforced by Open.</remarks>
    public override int ConnectionTimeout => _builder.ConnectTimeout;

    /// <summary>Default command timeout (seconds) inherited by commands created from this connection.</summary>
    public int DefaultCommandTimeout => _builder.CommandTimeout;

    internal DatabricksConnectionStringBuilder Settings => _builder;

    internal IDatabricksTransport Transport
        => _transport ?? throw new InvalidOperationException("The connection is not open.");

    /// <summary>
    /// Whether this connection can begin a transaction — i.e. whether its transport maintains a
    /// session that <c>BEGIN TRANSACTION</c> state can live in.
    /// </summary>
    /// <remarks>
    /// Readable before the connection is opened, so callers that must plan a unit of work ahead
    /// of time (an ORM deciding between an explicit transaction and a self-contained
    /// <c>BEGIN ATOMIC ... END;</c> block) can do so without connecting. The default REST
    /// transport is stateless and returns <see langword="false" />; the Thrift transport returns
    /// <see langword="true" />.
    /// </remarks>
    public bool SupportsTransactions
        => _transport?.SupportsTransactions ?? DeclaredTransportSupportsTransactions;

    /// <summary>
    /// What <see cref="SupportsTransactions" /> reports until the transport actually exists. Set
    /// alongside <see cref="TransportFactory" /> by whoever installs a transport.
    /// </summary>
    internal bool DeclaredTransportSupportsTransactions { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Genuinely synchronous: credential acquisition uses synchronous HTTP. Prefer
    /// <see cref="OpenAsync(CancellationToken)"/> in async code paths.
    /// </remarks>
    public override void Open()
    {
        if (_state == ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is already open.");
        }

        _builder.Validate();
        _catalog = _builder.Catalog;
        _schema = _builder.Schema;

        _state = ConnectionState.Connecting;
        try
        {
            if (TransportFactory is not null)
            {
                _transport = TransportFactory(this);
            }
            else
            {
                _authenticator = CreateAuthenticator();
                _transport = CreateRestTransport();

                // Eagerly acquire credentials so misconfiguration surfaces at Open time.
                using var connectCts = new CancellationTokenSource();
                if (_builder.ConnectTimeout > 0)
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(_builder.ConnectTimeout));
                }

                _authenticator.GetToken(connectCts.Token);
            }

            _state = ConnectionState.Open;
        }
        catch
        {
            DisposeTransport();
            _state = ConnectionState.Closed;
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is already open.");
        }

        _builder.Validate();
        _catalog = _builder.Catalog;
        _schema = _builder.Schema;

        _state = ConnectionState.Connecting;
        try
        {
            if (TransportFactory is not null)
            {
                _transport = TransportFactory(this);
            }
            else
            {
                _authenticator = CreateAuthenticator();
                _transport = CreateRestTransport();

                // Eagerly acquire credentials so misconfiguration surfaces at Open time.
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_builder.ConnectTimeout > 0)
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(_builder.ConnectTimeout));
                }

                await _authenticator.GetTokenAsync(connectCts.Token).ConfigureAwait(false);
            }

            _state = ConnectionState.Open;
        }
        catch
        {
            await DisposeTransportAsync().ConfigureAwait(false);
            _state = ConnectionState.Closed;
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>Genuinely synchronous: transport teardown has a synchronous path.</remarks>
    public override void Close()
    {
        AbandonTransaction();
        DisposeTransport();
        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override async Task CloseAsync()
    {
        AbandonTransaction();
        await DisposeTransportAsync().ConfigureAwait(false);
        _state = ConnectionState.Closed;
    }

    /// <summary>
    /// Ends any active transaction locally. Closing the connection ends the server-side
    /// session, which discards uncommitted work — there is no point issuing a ROLLBACK
    /// over a transport that is about to be torn down.
    /// </summary>
    private void AbandonTransaction()
    {
        _transaction?.MarkAbandoned();
        _transaction = null;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A transaction is active.</exception>
    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        EnsureOpen();
        EnsureNoActiveTransaction(nameof(ChangeDatabase));
        _schema = databaseName;
    }

    /// <summary>Changes the default catalog for subsequent statements on this connection.</summary>
    /// <exception cref="InvalidOperationException">A transaction is active.</exception>
    public void ChangeCatalog(string catalogName)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogName);
        EnsureOpen();
        EnsureNoActiveTransaction(nameof(ChangeCatalog));
        _catalog = catalogName;
    }

    /// <summary>
    /// Guards operations that would emit a <c>USE</c> statement on the session. Databricks
    /// does not allow metadata operations inside an interactive transaction, so changing the
    /// catalog or schema mid-transaction would fail at the server with a confusing error.
    /// </summary>
    private void EnsureNoActiveTransaction(string operation)
    {
        if (CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                $"{operation} cannot be called while a transaction is active: Databricks does not "
                + "support metadata operations inside an interactive transaction. Commit or roll "
                + "back the transaction first.");
        }
    }

    /// <summary>Creates a command associated with this connection.</summary>
    public new DatabricksCommand CreateCommand() => new(this);

    /// <inheritdoc />
    public override DataTable GetSchema() => GetSchema(DatabricksSchemaProvider.MetaDataCollections);

    /// <inheritdoc />
    public override DataTable GetSchema(string collectionName) => GetSchema(collectionName, []);

    /// <inheritdoc />
    /// <remarks>
    /// Supported collections: <c>MetaDataCollections</c>, <c>Catalogs</c> (restriction: catalog),
    /// <c>Schemas</c> (catalog, schema), <c>Tables</c> / <c>Views</c> (catalog, schema, table),
    /// and <c>Columns</c> (catalog, schema, table, column), backed by
    /// <c>system.information_schema</c>.
    /// </remarks>
    public override DataTable GetSchema(string collectionName, string?[] restrictionValues)
    {
        ArgumentException.ThrowIfNullOrEmpty(collectionName);
        if (!string.Equals(collectionName, DatabricksSchemaProvider.MetaDataCollections, StringComparison.OrdinalIgnoreCase))
        {
            EnsureOpen();
        }

        return DatabricksSchemaProvider.GetSchema(this, collectionName, restrictionValues);
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => CreateCommand();

    /// <inheritdoc />
    /// <remarks>
    /// Databricks supports interactive transactions (<c>BEGIN TRANSACTION</c> …
    /// <c>COMMIT</c>/<c>ROLLBACK</c>) as session state, so this requires a transport that
    /// maintains a session — the Thrift transport. On the stateless REST transport there is
    /// nowhere for the transaction to live and this throws; submit a self-contained
    /// <c>BEGIN ATOMIC ... END;</c> block as a single statement instead.
    /// See <see cref="DatabricksTransaction"/> for the requirements Databricks places on
    /// transactional tables.
    /// </remarks>
    /// <exception cref="NotSupportedException">The transport does not maintain a session.</exception>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or a transaction is already active.
    /// </exception>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        EnsureOpen();
        if (!Transport.SupportsTransactions)
        {
            throw new NotSupportedException(
                "The REST (Statement Execution API) transport is stateless and cannot hold "
                + "transaction state. Install the GlutenFree.Databricks.AdoNet.Thrift package and "
                + "call UseThriftTransport() to use interactive transactions, or submit a "
                + "'BEGIN ATOMIC ... END;' block as a single statement for an atomic multi-statement "
                + "unit of work.");
        }

        if (_transaction is { IsCompleted: false })
        {
            throw new InvalidOperationException(
                "A transaction is already active on this connection. Databricks allows only one "
                + "transaction at a time per session; commit or roll back the current transaction first.");
        }

        ExecuteControlStatement(DatabricksTransactionSql.Begin);
        var transaction = new DatabricksTransaction(this, isolationLevel);
        _transaction = transaction;
        return transaction;
    }

    /// <summary>The transaction currently active on this connection, if any.</summary>
    public DatabricksTransaction? CurrentTransaction => _transaction is { IsCompleted: false } t ? t : null;

    /// <summary>
    /// Executes a transaction-control statement (<c>BEGIN TRANSACTION</c>, <c>COMMIT</c>,
    /// <c>ROLLBACK</c>) on this connection's session, discarding any result.
    /// </summary>
    internal void ExecuteControlStatement(string sql)
    {
        using var command = CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Asynchronous counterpart of <see cref="ExecuteControlStatement"/>.</summary>
    internal async Task ExecuteControlStatementAsync(string sql, CancellationToken cancellationToken)
    {
        var command = CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Clears the active transaction once it completes.</summary>
    internal void ClearTransaction(DatabricksTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
        {
            _transaction = null;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal void EnsureOpen()
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to perform this operation.");
        }
    }

    private IDatabricksAuthenticator CreateAuthenticator() => _builder.AuthType switch
    {
        DatabricksAuthType.Pat => new PatAuthenticator(_builder.Token),
        // Shared per credential set: OAuth token caches survive across connections.
        DatabricksAuthType.OAuthM2M => DatabricksSharedResources.GetOAuthAuthenticator(
            _builder.Host, _builder.ClientId, _builder.ClientSecret),
        _ => throw new NotSupportedException($"AuthType '{_builder.AuthType}' is not supported."),
    };

    private RestStatementTransport CreateRestTransport()
    {
        // Only reject when the cluster path is the sole endpoint: an explicit WarehouseId
        // wins over HttpPath (documented precedence), so Validate() and Open() agree.
        if (_builder.IsAllPurposeClusterPath && _builder.EffectiveWarehouseId.Length == 0)
        {
            throw new NotSupportedException(
                "The HttpPath points at an all-purpose cluster, which only speaks the Thrift protocol. "
                + "Install the GlutenFree.Databricks.AdoNet.Thrift package and call UseThriftTransport() "
                + "before opening the connection; the default REST transport supports SQL warehouses only.");
        }

        return new(
            _builder.Host,
            _authenticator!,
            maxRetries: _builder.MaxRetries,
            retryBaseDelay: TimeSpan.FromMilliseconds(_builder.RetryBaseDelay),
            loggerFactory: LoggerFactory);
    }

    private async ValueTask DisposeTransportAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        // Authenticators are process-shared (OAuth token caches survive across connections)
        // and are never disposed here.
        _authenticator = null;
    }

    /// <summary>Synchronous counterpart of <see cref="DisposeTransportAsync"/>.</summary>
    private void DisposeTransport()
    {
        if (_transport is not null)
        {
            _transport.Dispose();
            _transport = null;
        }

        // Authenticators are process-shared (OAuth token caches survive across connections)
        // and are never disposed here.
        _authenticator = null;
    }
}
