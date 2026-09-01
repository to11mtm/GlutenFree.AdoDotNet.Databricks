using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Ipc;
using GlutenFree.Databricks.AdoNet.Internal;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// Forward-only reader over a Databricks statement result. Streams ARROW_STREAM chunks
/// (inline attachments or external links) or JSON_ARRAY rows, converting values per the
/// result manifest's schema.
/// </summary>
public sealed class DatabricksDataReader : DbDataReader
{
    private readonly IDatabricksTransport _transport;
    private readonly string _statementId;
    private readonly ColumnInfo[] _columns;
    private readonly int _totalChunkCount;
    private readonly long _totalRowCount;

    // Set when created with CommandBehavior.CloseConnection: closed along with the reader.
    private readonly DatabricksConnection? _connectionToClose;

    private readonly Queue<ExternalLink> _pendingLinks = new();
    private ResultData? _pendingInline;
    private int _highestChunkSeen = -1;

    // Exactly one of these is active per block.
    private IArrowArrayStream? _arrowReader;
    private RecordBatch? _arrowBatch;
    private IReadOnlyList<IReadOnlyList<string?>>? _jsonRows;

    private int _rowInBlock = -1;
    private bool _closed;

    internal DatabricksDataReader(
        IDatabricksTransport transport,
        StatementResponse response,
        DatabricksConnection? connectionToClose = null)
    {
        _transport = transport;
        _connectionToClose = connectionToClose;
        _statementId = response.StatementId ?? string.Empty;
        var manifest = response.Manifest;
        _columns = manifest?.Schema?.Columns?.OrderBy(c => c.Position).ToArray() ?? [];
        _totalChunkCount = manifest?.TotalChunkCount ?? 0;
        _totalRowCount = manifest?.TotalRowCount ?? 0;

        if (response.Result is not null)
        {
            _pendingInline = response.Result;
        }
    }

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override int FieldCount => _columns.Length;

    /// <inheritdoc />
    public override bool HasRows => _totalRowCount > 0;

    /// <inheritdoc />
    public override bool IsClosed => _closed;

    /// <inheritdoc />
    public override int RecordsAffected => -1;

    /// <summary>The server-side statement id backing this result.</summary>
    public string StatementId => _statementId;

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc />
    /// <remarks>Genuinely synchronous; prefer <see cref="ReadAsync(CancellationToken)"/>.</remarks>
    public override bool Read()
    {
        ThrowIfClosed();
        while (true)
        {
            var blockCount = _arrowBatch?.Length ?? _jsonRows?.Count ?? -1;
            if (blockCount >= 0 && _rowInBlock + 1 < blockCount)
            {
                _rowInBlock++;
                return true;
            }

            if (!AdvanceBlock())
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        while (true)
        {
            var blockCount = _arrowBatch?.Length ?? _jsonRows?.Count ?? -1;
            if (blockCount >= 0 && _rowInBlock + 1 < blockCount)
            {
                _rowInBlock++;
                return true;
            }

            if (!await AdvanceBlockAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <inheritdoc />
    public override string GetName(int ordinal) => _columns[ordinal].Name ?? string.Empty;

    /// <inheritdoc />
    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _columns.Length; i++)
        {
            if (string.Equals(_columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"Column '{name}' was not found in the result.");
    }

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal)
        => _columns[ordinal].TypeText ?? _columns[ordinal].TypeName ?? "STRING";

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal) => DatabricksTypeMap.GetFieldType(_columns[ordinal]);

    /// <inheritdoc />
    public override object GetValue(int ordinal)
    {
        ThrowIfNoRow();
        var column = _columns[ordinal];
        if (_arrowBatch is not null)
        {
            return DatabricksTypeMap.ConvertArrowValue(_arrowBatch.Column(ordinal), _rowInBlock, column);
        }

        return DatabricksTypeMap.ConvertJsonValue(_jsonRows![_rowInBlock][ordinal], column);
    }

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal)
    {
        ThrowIfNoRow();
        return _arrowBatch is not null
            ? _arrowBatch.Column(ordinal).IsNull(_rowInBlock)
            : _jsonRows![_rowInBlock][ordinal] is null;
    }

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));

    /// <summary>Gets the value of the column as a signed byte (Databricks TINYINT).</summary>
    public sbyte GetSByte(int ordinal) => (sbyte)GetValue(ordinal);

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    /// <inheritdoc />
    public override long GetInt64(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            sbyte b => b,
            _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            decimal m => m,
            SqlDecimal sd => sd.Value, // May throw OverflowException; use GetSqlDecimal instead.
            _ => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Gets a DECIMAL column value with full 38-digit precision, without the
    /// <see cref="decimal"/> overflow risk of <see cref="GetDecimal"/>.
    /// </summary>
    public SqlDecimal GetSqlDecimal(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            SqlDecimal sd => sd,
            decimal m => new SqlDecimal(m),
            string s => SqlDecimal.Parse(s),
            _ => throw new InvalidCastException($"Column {ordinal} is not a DECIMAL column."),
        };
    }

    /// <summary>
    /// Gets a DECIMAL column value as an arbitrary-precision <see cref="DatabricksDecimal"/>,
    /// lossless for any Databricks DECIMAL precision/scale.
    /// </summary>
    public DatabricksDecimal GetDatabricksDecimal(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            decimal m => DatabricksDecimal.FromDecimal(m),
            SqlDecimal sd => DatabricksDecimal.FromSqlDecimal(sd),
            string s => DatabricksDecimal.Parse(s),
            sbyte or short or int or long => new DatabricksDecimal(
                Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture), 0),
            _ => throw new InvalidCastException($"Column {ordinal} is not a DECIMAL column."),
        };
    }

    /// <inheritdoc />
    public override string GetString(int ordinal)
    {
        var value = GetValue(ordinal);
        return value as string
            ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
    }

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            DateTime dt => dt,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            _ => Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Gets a DATE column value as <see cref="DateOnly"/>.</summary>
    public DateOnly GetDateOnly(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => throw new InvalidCastException($"Column {ordinal} is not a DATE column."),
        };
    }

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));

    /// <inheritdoc />
    public override char GetChar(int ordinal) => GetString(ordinal)[0];

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);
        if (buffer is null)
        {
            return bytes.Length;
        }

        var available = Math.Max(bytes.Length - (int)dataOffset, 0);
        var toCopy = Math.Min(length, available);
        System.Array.Copy(bytes, dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var chars = GetString(ordinal);
        if (buffer is null)
        {
            return chars.Length;
        }

        var available = Math.Max(chars.Length - (int)dataOffset, 0);
        var toCopy = Math.Min(length, available);
        chars.CopyTo((int)dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    /// <inheritdoc />
    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is T typed)
        {
            return typed;
        }

        if (typeof(T) == typeof(SqlDecimal))
        {
            return (T)(object)GetSqlDecimal(ordinal);
        }

        if (typeof(T) == typeof(DatabricksDecimal))
        {
            return (T)(object)GetDatabricksDecimal(ordinal);
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)GetString(ordinal);
        }

        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <inheritdoc />
    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable");
        table.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        table.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        table.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        table.Columns.Add("DataTypeName", typeof(string));
        table.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(int));
        table.Columns.Add(SchemaTableColumn.NumericScale, typeof(int));
        table.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));

        for (var i = 0; i < _columns.Length; i++)
        {
            var column = _columns[i];
            table.Rows.Add(
                column.Name,
                i,
                DatabricksTypeMap.GetFieldType(column),
                column.TypeText ?? column.TypeName,
                column.TypePrecision,
                column.TypeScale,
                true);
        }

        return table;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Releases the current Arrow batch/stream immediately (same resources as Dispose).
    /// When the reader was created with <see cref="System.Data.CommandBehavior.CloseConnection"/>,
    /// closing it also closes the owning connection.
    /// </remarks>
    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _arrowBatch?.Dispose();
        _arrowBatch = null;
        _arrowReader?.Dispose();
        _arrowReader = null;
        // Also release the managed JSON/inline buffers: a closed reader that stays in
        // scope must not retain an entire result payload.
        _jsonRows = null;
        _pendingInline?.ArrowStream?.Dispose();
        _pendingInline = null;
        _pendingLinks.Clear();
        _rowInBlock = -1;
        _connectionToClose?.Close();
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

    /// <summary>
    /// For DML statements: reads the <c>num_affected_rows</c> result column if present, else -1.
    /// </summary>
    internal async Task<int> GetAffectedRowCountAsync(CancellationToken cancellationToken)
    {
        var ordinal = FindAffectedRowsOrdinal();
        if (ordinal >= 0
            && await ReadAsync(cancellationToken).ConfigureAwait(false)
            && !IsDBNull(ordinal))
        {
            // num_affected_rows is a BIGINT; fail explicitly rather than silently wrapping
            // counts above int.MaxValue (ExecuteNonQuery cannot represent them).
            return checked((int)GetInt64(ordinal));
        }

        return -1;
    }

    /// <summary>Synchronous counterpart of <see cref="GetAffectedRowCountAsync"/>.</summary>
    internal int GetAffectedRowCount()
    {
        var ordinal = FindAffectedRowsOrdinal();
        if (ordinal >= 0 && Read() && !IsDBNull(ordinal))
        {
            return checked((int)GetInt64(ordinal));
        }

        return -1;
    }

    private int FindAffectedRowsOrdinal()
    {
        for (var i = 0; i < _columns.Length; i++)
        {
            if (string.Equals(_columns[i].Name, "num_affected_rows", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task<bool> AdvanceBlockAsync(CancellationToken cancellationToken)
    {
        // 1. More record batches within the current Arrow chunk stream?
        if (_arrowReader is not null)
        {
            var next = await _arrowReader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (next is not null)
            {
                SetArrowBatch(next);
                return true;
            }

            _arrowReader.Dispose();
            _arrowReader = null;
        }

        ClearBlock();

        while (true)
        {
            // 2. Inline payload (initial response or a fetched chunk).
            if (_pendingInline is not null)
            {
                var inline = _pendingInline;
                _pendingInline = null;
                _highestChunkSeen = Math.Max(_highestChunkSeen, inline.ChunkIndex);

                if (inline.ArrowStream is { } stream)
                {
                    var batch = await stream.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
                    if (batch is null)
                    {
                        stream.Dispose();
                        continue;
                    }

                    _arrowReader = stream;
                    SetArrowBatch(batch);
                    return true;
                }

                if (inline.ExternalLinks is { Count: > 0 } links)
                {
                    foreach (var link in links)
                    {
                        _pendingLinks.Enqueue(link);
                    }

                    continue;
                }

                if (inline.Attachment is { Length: > 0 } attachment)
                {
                    return await StartArrowStreamAsync(Convert.FromBase64String(attachment), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (inline.DataArray is { Count: > 0 } rows)
                {
                    _jsonRows = rows;
                    _rowInBlock = -1;
                    return true;
                }

                continue; // Empty chunk; look for the next one.
            }

            // 3. Pending external link downloads.
            if (_pendingLinks.Count > 0)
            {
                var link = _pendingLinks.Dequeue();
                _highestChunkSeen = Math.Max(_highestChunkSeen, link.ChunkIndex);
                var bytes = await _transport.DownloadExternalLinkAsync(link, cancellationToken)
                    .ConfigureAwait(false);

                if (IsArrowStream(bytes))
                {
                    if (await StartArrowStreamAsync(bytes, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    continue;
                }

                var rows = JsonSerializer.Deserialize<List<List<string?>>>(bytes);
                if (rows is { Count: > 0 })
                {
                    _jsonRows = rows;
                    _rowInBlock = -1;
                    return true;
                }

                continue;
            }

            // 4. More chunks to request from the statement result?
            var nextChunk = _highestChunkSeen + 1;
            if (nextChunk < _totalChunkCount && _statementId.Length > 0)
            {
                _pendingInline = await _transport
                    .GetResultChunkAsync(_statementId, nextChunk, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            return false;
        }
    }

    private async Task<bool> StartArrowStreamAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var reader = new ArrowStreamReader(new MemoryStream(bytes), new CompressionCodecFactory());
        var batch = await reader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            reader.Dispose();
            return false;
        }

        _arrowReader = reader;
        SetArrowBatch(batch);
        return true;
    }

    private bool StartArrowStream(byte[] bytes)
    {
        var reader = new ArrowStreamReader(new MemoryStream(bytes), new CompressionCodecFactory());
        var batch = reader.ReadNextRecordBatch();
        if (batch is null)
        {
            reader.Dispose();
            return false;
        }

        _arrowReader = reader;
        SetArrowBatch(batch);
        return true;
    }

    /// <summary>Synchronous mirror of <see cref="AdvanceBlockAsync"/> using the sync transport path.</summary>
    private bool AdvanceBlock()
    {
        // 1. More record batches within the current Arrow chunk stream?
        if (_arrowReader is not null)
        {
            var next = ReadNextBatchSync(_arrowReader);
            if (next is not null)
            {
                SetArrowBatch(next);
                return true;
            }

            _arrowReader.Dispose();
            _arrowReader = null;
        }

        ClearBlock();

        while (true)
        {
            // 2. Inline payload (initial response or a fetched chunk).
            if (_pendingInline is not null)
            {
                var inline = _pendingInline;
                _pendingInline = null;
                _highestChunkSeen = Math.Max(_highestChunkSeen, inline.ChunkIndex);

                if (inline.ArrowStream is { } stream)
                {
                    var batch = ReadNextBatchSync(stream);
                    if (batch is null)
                    {
                        stream.Dispose();
                        continue;
                    }

                    _arrowReader = stream;
                    SetArrowBatch(batch);
                    return true;
                }

                if (inline.ExternalLinks is { Count: > 0 } links)
                {
                    foreach (var link in links)
                    {
                        _pendingLinks.Enqueue(link);
                    }

                    continue;
                }

                if (inline.Attachment is { Length: > 0 } attachment)
                {
                    return StartArrowStream(Convert.FromBase64String(attachment));
                }

                if (inline.DataArray is { Count: > 0 } rows)
                {
                    _jsonRows = rows;
                    _rowInBlock = -1;
                    return true;
                }

                continue; // Empty chunk; look for the next one.
            }

            // 3. Pending external link downloads.
            if (_pendingLinks.Count > 0)
            {
                var link = _pendingLinks.Dequeue();
                _highestChunkSeen = Math.Max(_highestChunkSeen, link.ChunkIndex);
                var bytes = _transport.DownloadExternalLink(link, CancellationToken.None);

                if (IsArrowStream(bytes))
                {
                    if (StartArrowStream(bytes))
                    {
                        return true;
                    }

                    continue;
                }

                var rows = JsonSerializer.Deserialize<List<List<string?>>>(bytes);
                if (rows is { Count: > 0 })
                {
                    _jsonRows = rows;
                    _rowInBlock = -1;
                    return true;
                }

                continue;
            }

            // 4. More chunks to request from the statement result?
            var nextChunk = _highestChunkSeen + 1;
            if (nextChunk < _totalChunkCount && _statementId.Length > 0)
            {
                _pendingInline = _transport.GetResultChunk(_statementId, nextChunk, CancellationToken.None);
                continue;
            }

            return false;
        }
    }

    private void SetArrowBatch(RecordBatch batch)
    {
        _arrowBatch?.Dispose();
        _arrowBatch = batch;
        _jsonRows = null;
        _rowInBlock = -1;
    }

    private void ClearBlock()
    {
        _arrowBatch?.Dispose();
        _arrowBatch = null;
        _jsonRows = null;
        _rowInBlock = -1;
    }

    private static bool IsArrowStream(ReadOnlySpan<byte> bytes)
        // Arrow IPC streams begin with a 4-byte continuation marker 0xFFFFFFFF
        // followed by the schema message; JSON chunks begin with '['.
        => bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFF && bytes[2] == 0xFF && bytes[3] == 0xFF;

    /// <summary>
    /// Reads the next batch synchronously; see <see cref="ArrowSync.ReadNextBatch"/> for the
    /// sync-read/async-fallback split.
    /// </summary>
    private static RecordBatch? ReadNextBatchSync(IArrowArrayStream stream)
        => ArrowSync.ReadNextBatch(stream);

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }
    }

    private void ThrowIfNoRow()
    {
        ThrowIfClosed();
        if (_rowInBlock < 0 || (_arrowBatch is null && _jsonRows is null))
        {
            throw new InvalidOperationException("No current row. Call Read() first.");
        }
    }
}
