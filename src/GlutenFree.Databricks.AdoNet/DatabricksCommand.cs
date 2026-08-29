using System.Data;
using System.Data.Common;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// A SQL statement executed against a Databricks SQL warehouse.
/// Only <see cref="CommandType.Text"/> is supported; parameters use <c>:name</c> markers
/// bound server-side via the Statement Execution API.
/// </summary>
public sealed class DatabricksCommand : DbCommand
{
    private DatabricksConnection? _connection;
    private string _commandText = string.Empty;
    private int? _commandTimeout;
    private CancellationTokenSource? _userCancellation;

    /// <summary>Creates a command with no connection.</summary>
    public DatabricksCommand()
    {
    }

    /// <summary>Creates a command bound to a connection.</summary>
    public DatabricksCommand(DatabricksConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Creates a command with text, bound to a connection.</summary>
    public DatabricksCommand(string commandText, DatabricksConnection connection)
    {
        _commandText = commandText;
        _connection = connection;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int CommandTimeout
    {
        get => _commandTimeout ?? _connection?.DefaultCommandTimeout ?? 0;
        set => _commandTimeout = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "CommandTimeout must be non-negative.");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">For any value other than <see cref="CommandType.Text"/>.</exception>
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException("Databricks commands support CommandType.Text only.");
            }
        }
    }

    /// <inheritdoc />
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    /// <summary>The parameters for this command.</summary>
    public new DatabricksParameterCollection Parameters { get; } = [];

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => Parameters;

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value switch
        {
            null => null,
            DatabricksConnection databricks => databricks,
            _ => throw new InvalidCastException($"Expected a {nameof(DatabricksConnection)}."),
        };
    }

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => null;
        set
        {
            if (value is not null)
            {
                throw new NotSupportedException("Databricks SQL does not support transactions.");
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Signals cancellation of the in-flight execution; the transport then issues a
    /// best-effort server-side statement cancel.
    /// </remarks>
    public override void Cancel() => _userCancellation?.Cancel();

    /// <inheritdoc />
    public override void Prepare()
    {
        // The Statement Execution API has no prepare step; validation happens at execute time.
    }

    /// <inheritdoc />
    /// <remarks>Genuinely synchronous; prefer <see cref="ExecuteNonQueryAsync(CancellationToken)"/>.</remarks>
    public override int ExecuteNonQuery()
    {
        using var reader = ExecuteReaderInternal();
        return reader.GetAffectedRowCount();
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteReaderInternalAsync(cancellationToken).ConfigureAwait(false);
        return await reader.GetAffectedRowCountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Genuinely synchronous; prefer <see cref="ExecuteScalarAsync(CancellationToken)"/>.</remarks>
    public override object? ExecuteScalar()
    {
        using var reader = ExecuteReaderInternal();
        return reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteReaderInternalAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0
            ? reader.GetValue(0)
            : null;
    }

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteReaderInternal();

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
        => await ExecuteReaderInternalAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Executes the command and returns a <see cref="DatabricksDataReader"/>.</summary>
    /// <remarks>Genuinely synchronous; prefer <see cref="ExecuteReaderAsync(CancellationToken)"/>.</remarks>
    public new DatabricksDataReader ExecuteReader() => ExecuteReaderInternal();

    /// <summary>Executes the command and returns a <see cref="DatabricksDataReader"/>.</summary>
    public new async Task<DatabricksDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
        => await ExecuteReaderInternalAsync(cancellationToken).ConfigureAwait(false);

    private async Task<DatabricksDataReader> ExecuteReaderInternalAsync(CancellationToken cancellationToken)
    {
        var (connection, request) = PrepareExecution();
        var timeout = TimeSpan.FromSeconds(CommandTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StatementResponse response;
        try
        {
            _userCancellation = cancellation;
            response = await connection.Transport
                .ExecuteStatementAsync(request, timeout, cancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _userCancellation = null;
        }

        return new DatabricksDataReader(connection.Transport, response);
    }

    private DatabricksDataReader ExecuteReaderInternal()
    {
        var (connection, request) = PrepareExecution();
        var timeout = TimeSpan.FromSeconds(CommandTimeout);
        using var cancellation = new CancellationTokenSource();
        StatementResponse response;
        try
        {
            _userCancellation = cancellation;
            // Genuinely synchronous transport path (HttpClient.Send); no sync-over-async.
            response = connection.Transport.ExecuteStatement(request, timeout, cancellation.Token);
        }
        finally
        {
            _userCancellation = null;
        }

        return new DatabricksDataReader(connection.Transport, response);
    }

    private (DatabricksConnection Connection, StatementRequest Request) PrepareExecution()
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The command has no associated connection.");
        connection.EnsureOpen();
        if (_commandText.Length == 0)
        {
            throw new InvalidOperationException("CommandText has not been set.");
        }

        var settings = connection.Settings;
        var request = new StatementRequest
        {
            Statement = _commandText,
            WarehouseId = settings.EffectiveWarehouseId,
            Catalog = NullIfEmpty(connection.Catalog),
            Schema = NullIfEmpty(connection.Database),
            Parameters = Parameters.ToStatementParameters(),
            Format = ResolveFormat(settings),
            Disposition = ResolveDisposition(settings),
        };
        return (connection, request);
    }

    private static string ResolveFormat(DatabricksConnectionStringBuilder settings)
        => settings.ResultFormat == DatabricksResultFormat.Arrow ? "ARROW_STREAM" : "JSON_ARRAY";

    private static string ResolveDisposition(DatabricksConnectionStringBuilder settings)
        => settings.Disposition switch
        {
            DatabricksDisposition.Inline when settings.ResultFormat == DatabricksResultFormat.Arrow
                => throw new NotSupportedException(
                    "Disposition=Inline requires ResultFormat=Json; the Statement Execution API " +
                    "only serves ARROW_STREAM results via external links."),
            DatabricksDisposition.Inline => "INLINE",
            DatabricksDisposition.ExternalLinks => "EXTERNAL_LINKS",
            // Auto: pick the only valid pairing per format.
            _ => settings.ResultFormat == DatabricksResultFormat.Arrow ? "EXTERNAL_LINKS" : "INLINE",
        };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => new DatabricksParameter();
}
