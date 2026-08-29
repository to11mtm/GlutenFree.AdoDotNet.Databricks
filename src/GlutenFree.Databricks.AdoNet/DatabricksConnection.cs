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
/// The underlying REST transport is stateless HTTP: <see cref="Open()"/> validates
/// configuration and prepares the transport rather than establishing a socket session.
/// Databricks SQL does not support multi-statement transactions, so
/// <see cref="BeginDbTransaction"/> throws <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class DatabricksConnection : DbConnection
{
    private DatabricksConnectionStringBuilder _builder = new();
    private IDatabricksTransport? _transport;
    private IDatabricksAuthenticator? _authenticator;
    private ConnectionState _state = ConnectionState.Closed;
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

    /// <summary>Default command timeout (seconds) inherited by commands created from this connection.</summary>
    public int DefaultCommandTimeout => _builder.CommandTimeout;

    internal DatabricksConnectionStringBuilder Settings => _builder;

    internal IDatabricksTransport Transport
        => _transport ?? throw new InvalidOperationException("The connection is not open.");

    /// <inheritdoc />
    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

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
                _transport = new RestStatementTransport(
                    _builder.Host,
                    _authenticator,
                    maxRetries: _builder.MaxRetries,
                    retryBaseDelay: TimeSpan.FromMilliseconds(_builder.RetryBaseDelay),
                    loggerFactory: LoggerFactory);

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
    public override void Close() => CloseAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task CloseAsync()
    {
        await DisposeTransportAsync().ConfigureAwait(false);
        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        EnsureOpen();
        _schema = databaseName;
    }

    /// <summary>Changes the default catalog for subsequent statements on this connection.</summary>
    public void ChangeCatalog(string catalogName)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogName);
        EnsureOpen();
        _catalog = catalogName;
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
    /// <exception cref="NotSupportedException">
    /// Always: Databricks SQL does not support multi-statement transactions.
    /// </exception>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => throw new NotSupportedException(
            "Databricks SQL does not support multi-statement transactions. " +
            "Each statement is executed atomically by the server.");

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
        DatabricksAuthType.OAuthM2M => new OAuthM2MAuthenticator(_builder.Host, _builder.ClientId, _builder.ClientSecret),
        _ => throw new NotSupportedException($"AuthType '{_builder.AuthType}' is not supported."),
    };

    private async ValueTask DisposeTransportAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        if (_authenticator is IDisposable disposableAuth)
        {
            disposableAuth.Dispose();
        }

        _authenticator = null;
    }
}
