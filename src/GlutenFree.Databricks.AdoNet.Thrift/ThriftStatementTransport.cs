using System.Collections.Concurrent;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.Drivers.Apache;
using Apache.Arrow.Adbc.Drivers.Apache.Spark;
using Apache.Arrow.Adbc.Drivers.Databricks;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Thrift;

/// <summary>
/// <see cref="IDatabricksTransport"/> implementation backed by the Thrift (HiveServer2)
/// protocol via the Apache Arrow ADBC Databricks driver. Unlike the REST transport, this
/// maintains a real server-side session for the lifetime of the connection, so session
/// state (<c>USE</c>, session confs, temp views) persists across commands.
/// </summary>
public sealed class ThriftStatementTransport : IDatabricksTransport
{
    private readonly AdbcDatabase _database;
    private readonly AdbcConnection _connection;
    private readonly ConcurrentDictionary<string, AdbcStatement> _activeStatements = new();

    private string? _sessionCatalog;
    private string? _sessionSchema;

    /// <summary>
    /// Creates a transport with an open Thrift session against the given warehouse.
    /// </summary>
    /// <param name="host">Workspace base URL (https).</param>
    /// <param name="httpPath">Warehouse HTTP path, e.g. <c>/sql/1.0/warehouses/abc123</c>.</param>
    /// <param name="options">Authentication and driver options; see <see cref="ThriftTransportOptions"/>.</param>
    public ThriftStatementTransport(string host, string httpPath, ThriftTransportOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(httpPath);
        ArgumentNullException.ThrowIfNull(options);

        var hostUri = new Uri(host, UriKind.Absolute);
        if (hostUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The workspace host must use https; credentials must never be sent over plaintext http.",
                nameof(host));
        }

        var properties = new Dictionary<string, string>
        {
            [SparkParameters.HostName] = hostUri.Host,
            [SparkParameters.Port] = hostUri.IsDefaultPort ? "443" : hostUri.Port.ToString(),
            [SparkParameters.Path] = httpPath,
        };

        if (options.Token is { Length: > 0 } token)
        {
            properties[SparkParameters.AuthType] = SparkAuthTypeConstants.Token;
            properties[SparkParameters.Token] = token;
        }
        else if (options is { OAuthClientId.Length: > 0, OAuthClientSecret.Length: > 0 })
        {
            properties[SparkParameters.AuthType] = SparkAuthTypeConstants.OAuth;
            properties[DatabricksParameters.OAuthGrantType] = DatabricksConstants.OAuthGrantTypes.ClientCredentials;
            properties[DatabricksParameters.OAuthClientId] = options.OAuthClientId!;
            properties[DatabricksParameters.OAuthClientSecret] = options.OAuthClientSecret!;
        }
        else
        {
            throw new ArgumentException(
                "Either a personal access token or an OAuth client id/secret pair is required.",
                nameof(options));
        }

        if (options.ConnectTimeout > TimeSpan.Zero)
        {
            properties[SparkParameters.ConnectTimeoutMilliseconds] =
                ((long)options.ConnectTimeout.TotalMilliseconds).ToString();
        }

        foreach (var (key, value) in options.DriverOptions)
        {
            properties[key] = value;
        }

        _database = new DatabricksDriver().Open(properties);
        try
        {
            _connection = _database.Connect(new Dictionary<string, string>());
        }
        catch
        {
            _database.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<StatementResponse> ExecuteStatementAsync(
        StatementRequest request,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        await ApplySessionContextAsync(request, commandTimeout, cancellationToken, sync: false)
            .ConfigureAwait(false);

        var statement = CreateAdbcStatement(request, commandTimeout);
        var statementId = Guid.NewGuid().ToString("N");
        _activeStatements[statementId] = statement;
        try
        {
            using var cancelRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static s => TryCancel((AdbcStatement)s!), statement)
                : default;

            var result = await statement.ExecuteQueryAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await BuildResponseAsync(statementId, statement, result, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _activeStatements.TryRemove(statementId, out _);
            statement.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public StatementResponse ExecuteStatement(
        StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ApplySessionContextAsync(request, commandTimeout, cancellationToken, sync: true)
            .GetAwaiter().GetResult();

        var statement = CreateAdbcStatement(request, commandTimeout);
        var statementId = Guid.NewGuid().ToString("N");
        _activeStatements[statementId] = statement;
        try
        {
            using var cancelRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static s => TryCancel((AdbcStatement)s!), statement)
                : default;

            var result = statement.ExecuteQuery();
            cancellationToken.ThrowIfCancellationRequested();
            return BuildResponseAsync(statementId, statement, result, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch
        {
            _activeStatements.TryRemove(statementId, out _);
            statement.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never called for this transport: results are delivered as a single streaming
    /// chunk (<see cref="ResultData.ArrowStream"/>), so the reader has no further
    /// chunks to request.
    /// </remarks>
    public Task<ResultData> GetResultChunkAsync(
        string statementId, int chunkIndex, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "The Thrift transport streams results inline and does not serve random-access chunks.");

    /// <inheritdoc />
    /// <remarks>Never called: CloudFetch downloads are handled inside the ADBC driver.</remarks>
    public Task<byte[]> DownloadExternalLinkAsync(ExternalLink link, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "The Thrift transport never surfaces external links; CloudFetch is handled by the ADBC driver.");

    /// <inheritdoc />
    public Task CancelStatementAsync(string statementId, CancellationToken cancellationToken)
    {
        if (_activeStatements.TryGetValue(statementId, out var statement))
        {
            TryCancel(statement);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        foreach (var (id, statement) in _activeStatements)
        {
            if (_activeStatements.TryRemove(id, out _))
            {
                try
                {
                    statement.Dispose();
                }
                catch
                {
                    // Best-effort teardown.
                }
            }
        }

        try
        {
            _connection.Dispose(); // Closes the Thrift session.
        }
        finally
        {
            _database.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Keeps the session's catalog/schema in line with the request. The REST transport
    /// passes these per statement; over Thrift they are session state, so replay
    /// <c>USE</c> statements only when they change.
    /// </summary>
    private async Task ApplySessionContextAsync(
        StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken, bool sync)
    {
        if (request.Catalog is { Length: > 0 } catalog
            && !string.Equals(catalog, _sessionCatalog, StringComparison.Ordinal))
        {
            await ExecuteUseAsync($"USE CATALOG {QuoteIdentifier(catalog)}", commandTimeout, cancellationToken, sync)
                .ConfigureAwait(false);
            _sessionCatalog = catalog;
            _sessionSchema = null; // Changing catalog resets the schema server-side.
        }

        if (request.Schema is { Length: > 0 } schema
            && !string.Equals(schema, _sessionSchema, StringComparison.Ordinal))
        {
            await ExecuteUseAsync($"USE SCHEMA {QuoteIdentifier(schema)}", commandTimeout, cancellationToken, sync)
                .ConfigureAwait(false);
            _sessionSchema = schema;
        }
    }

    private async Task ExecuteUseAsync(
        string sql, TimeSpan commandTimeout, CancellationToken cancellationToken, bool sync)
    {
        using var statement = _connection.CreateStatement();
        ApplyTimeout(statement, commandTimeout);
        statement.SqlQuery = sql;

        using var cancelRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => TryCancel((AdbcStatement)s!), statement)
            : default;

        if (sync)
        {
            statement.ExecuteUpdate();
        }
        else
        {
            await statement.ExecuteUpdateAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private AdbcStatement CreateAdbcStatement(StatementRequest request, TimeSpan commandTimeout)
    {
        if (request.Parameters is { Count: > 0 })
        {
            throw new NotSupportedException(
                "The Thrift transport does not support named parameters yet; use the REST transport for parameterized statements.");
        }

        var statement = _connection.CreateStatement();
        try
        {
            ApplyTimeout(statement, commandTimeout);
            statement.SqlQuery = request.Statement;
            return statement;
        }
        catch
        {
            statement.Dispose();
            throw;
        }
    }

    private static void ApplyTimeout(AdbcStatement statement, TimeSpan commandTimeout)
    {
        if (commandTimeout > TimeSpan.Zero)
        {
            statement.SetOption(
                ApacheParameters.QueryTimeoutSeconds,
                Math.Max(1, (long)commandTimeout.TotalSeconds).ToString());
        }
    }

    private async Task<StatementResponse> BuildResponseAsync(
        string statementId, AdbcStatement statement, QueryResult result, CancellationToken cancellationToken)
    {
        var stream = result.Stream;
        if (stream is null)
        {
            // DML/DDL executed without a result stream.
            _activeStatements.TryRemove(statementId, out _);
            statement.Dispose();
            return new StatementResponse
            {
                StatementId = statementId,
                Status = new StatementStatus { State = "SUCCEEDED" },
                Manifest = new ResultManifest { Format = "ARROW_STREAM", TotalChunkCount = 0, TotalRowCount = 0 },
            };
        }

        // Peek the first batch so empty results report HasRows correctly even though
        // the stream's total row count is unknown up front.
        var firstBatch = await stream.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        var columns = BuildColumns(stream.Schema);
        var totalRowCount = result.RowCount >= 0
            ? result.RowCount
            : firstBatch?.Length ?? 0;

        var owned = new OwnedArrowStream(stream, firstBatch, () =>
        {
            if (_activeStatements.TryRemove(statementId, out var s))
            {
                s.Dispose();
            }
        });

        return new StatementResponse
        {
            StatementId = statementId,
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "ARROW_STREAM",
                Schema = new ResultSchema { ColumnCount = columns.Count, Columns = columns },
                TotalChunkCount = 1,
                TotalRowCount = totalRowCount,
            },
            Result = new ResultData
            {
                ChunkIndex = 0,
                RowCount = totalRowCount,
                ArrowStream = owned,
            },
        };
    }

    private static void TryCancel(AdbcStatement statement)
    {
        try
        {
            statement.Cancel();
        }
        catch
        {
            // Cancellation is best-effort, matching the REST transport.
        }
    }

    private static string QuoteIdentifier(string identifier)
        => "`" + identifier.Replace("`", "``") + "`";

    private static IReadOnlyList<ColumnInfo> BuildColumns(Schema schema)
    {
        var columns = new List<ColumnInfo>(schema.FieldsList.Count);
        for (var i = 0; i < schema.FieldsList.Count; i++)
        {
            var field = schema.FieldsList[i];
            var (typeName, typeText, precision, scale) = MapArrowType(field.DataType);
            columns.Add(new ColumnInfo
            {
                Name = field.Name,
                TypeName = typeName,
                TypeText = typeText,
                TypePrecision = precision,
                TypeScale = scale,
                Position = i,
            });
        }

        return columns;
    }

    /// <summary>
    /// Reconstructs Databricks SQL type names from Arrow field types. The REST transport
    /// gets these from the result manifest; over ADBC only the Arrow schema is available.
    /// </summary>
    private static (string TypeName, string TypeText, int Precision, int Scale) MapArrowType(IArrowType type)
        => type switch
        {
            BooleanType => ("BOOLEAN", "BOOLEAN", 0, 0),
            Int8Type => ("TINYINT", "TINYINT", 0, 0),
            Int16Type => ("SMALLINT", "SMALLINT", 0, 0),
            Int32Type => ("INT", "INT", 0, 0),
            Int64Type => ("BIGINT", "BIGINT", 0, 0),
            FloatType => ("FLOAT", "FLOAT", 0, 0),
            DoubleType => ("DOUBLE", "DOUBLE", 0, 0),
            StringType => ("STRING", "STRING", 0, 0),
            BinaryType => ("BINARY", "BINARY", 0, 0),
            Date32Type or Date64Type => ("DATE", "DATE", 0, 0),
            TimestampType t => t.Timezone is null
                ? ("TIMESTAMP_NTZ", "TIMESTAMP_NTZ", 0, 0)
                : ("TIMESTAMP", "TIMESTAMP", 0, 0),
            Decimal128Type d => ("DECIMAL", $"DECIMAL({d.Precision},{d.Scale})", d.Precision, d.Scale),
            Decimal256Type d => ("DECIMAL", $"DECIMAL({d.Precision},{d.Scale})", d.Precision, d.Scale),
            ListType => ("ARRAY", "ARRAY", 0, 0),
            StructType => ("STRUCT", "STRUCT", 0, 0),
            MapType => ("MAP", "MAP", 0, 0),
            IntervalType => ("INTERVAL", "INTERVAL", 0, 0),
            _ => ("STRING", type.Name.ToUpperInvariant(), 0, 0),
        };

    /// <summary>
    /// Wraps the driver's result stream so the peeked first batch is replayed and the
    /// backing <see cref="AdbcStatement"/> is released exactly once when the reader
    /// disposes the stream.
    /// </summary>
    private sealed class OwnedArrowStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private RecordBatch? _firstBatch;
        private Action? _onDispose;

        public OwnedArrowStream(IArrowArrayStream inner, RecordBatch? firstBatch, Action onDispose)
        {
            _inner = inner;
            _firstBatch = firstBatch;
            _onDispose = onDispose;
        }

        public Schema Schema => _inner.Schema;

        public ValueTask<RecordBatch> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            if (_firstBatch is { } batch)
            {
                _firstBatch = null;
                return new ValueTask<RecordBatch>(batch);
            }

            return _inner.ReadNextRecordBatchAsync(cancellationToken);
        }

        public void Dispose()
        {
            _firstBatch?.Dispose();
            _firstBatch = null;
            _inner.Dispose();
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
