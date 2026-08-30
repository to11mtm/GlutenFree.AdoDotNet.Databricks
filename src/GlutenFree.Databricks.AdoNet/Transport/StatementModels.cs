using System.Text.Json.Serialization;

// DTO members mirror the documented Statement Execution API fields; per-member XML docs add no value.
#pragma warning disable CS1591

namespace GlutenFree.Databricks.AdoNet.Transport;

/// <summary>Request body for <c>POST /api/2.0/sql/statements</c>.</summary>
public sealed class StatementRequest
{
    [JsonPropertyName("statement")]
    public required string Statement { get; init; }

    [JsonPropertyName("warehouse_id")]
    public required string WarehouseId { get; init; }

    [JsonPropertyName("catalog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Catalog { get; init; }

    [JsonPropertyName("schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Schema { get; init; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatementParameter>? Parameters { get; init; }

    /// <summary>Server-side synchronous wait, e.g. <c>"10s"</c> (0–50s).</summary>
    [JsonPropertyName("wait_timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WaitTimeout { get; init; }

    /// <summary>Behavior when <see cref="WaitTimeout"/> elapses: <c>CONTINUE</c> or <c>CANCEL</c>.</summary>
    [JsonPropertyName("on_wait_timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OnWaitTimeout { get; init; }

    /// <summary><c>JSON_ARRAY</c>, <c>ARROW_STREAM</c>, or <c>CSV</c>.</summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    /// <summary><c>INLINE</c> or <c>EXTERNAL_LINKS</c>.</summary>
    [JsonPropertyName("disposition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Disposition { get; init; }
}

/// <summary>A named parameter passed to the Statement Execution API.</summary>
public sealed class StatementParameter
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>String representation of the value; <c>null</c> maps to SQL NULL.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Databricks SQL type name (e.g. <c>INT</c>, <c>STRING</c>, <c>TIMESTAMP</c>).</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }
}

/// <summary>Response body for statement submit/poll endpoints.</summary>
public sealed class StatementResponse
{
    [JsonPropertyName("statement_id")]
    public string? StatementId { get; init; }

    [JsonPropertyName("status")]
    public StatementStatus? Status { get; init; }

    [JsonPropertyName("manifest")]
    public ResultManifest? Manifest { get; init; }

    [JsonPropertyName("result")]
    public ResultData? Result { get; init; }
}

/// <summary>Statement status and error details.</summary>
public sealed class StatementStatus
{
    /// <summary><c>PENDING</c>, <c>RUNNING</c>, <c>SUCCEEDED</c>, <c>FAILED</c>, <c>CANCELED</c>, or <c>CLOSED</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("error")]
    public StatementError? Error { get; init; }
}

/// <summary>Error details reported for a failed statement.</summary>
public sealed class StatementError
{
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Result manifest: schema, chunk layout, and format.</summary>
public sealed class ResultManifest
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("schema")]
    public ResultSchema? Schema { get; init; }

    [JsonPropertyName("total_chunk_count")]
    public int TotalChunkCount { get; init; }

    [JsonPropertyName("total_row_count")]
    public long TotalRowCount { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("chunks")]
    public IReadOnlyList<ChunkInfo>? Chunks { get; init; }
}

/// <summary>Result schema description.</summary>
public sealed class ResultSchema
{
    [JsonPropertyName("column_count")]
    public int ColumnCount { get; init; }

    [JsonPropertyName("columns")]
    public IReadOnlyList<ColumnInfo>? Columns { get; init; }
}

/// <summary>A single result column description.</summary>
public sealed class ColumnInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Databricks type name, e.g. <c>INT</c>, <c>DECIMAL</c>, <c>ARRAY</c>.</summary>
    [JsonPropertyName("type_name")]
    public string? TypeName { get; init; }

    /// <summary>Full type text, e.g. <c>DECIMAL(38,2)</c>, <c>ARRAY&lt;INT&gt;</c>.</summary>
    [JsonPropertyName("type_text")]
    public string? TypeText { get; init; }

    [JsonPropertyName("type_precision")]
    public int TypePrecision { get; init; }

    [JsonPropertyName("type_scale")]
    public int TypeScale { get; init; }

    [JsonPropertyName("position")]
    public int Position { get; init; }
}

/// <summary>Metadata for a single result chunk.</summary>
public sealed class ChunkInfo
{
    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; init; }

    [JsonPropertyName("row_offset")]
    public long RowOffset { get; init; }

    [JsonPropertyName("row_count")]
    public long RowCount { get; init; }

    [JsonPropertyName("byte_count")]
    public long ByteCount { get; init; }
}

/// <summary>Result payload: inline rows, inline Arrow attachment, or external links.</summary>
public sealed class ResultData
{
    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; init; }

    [JsonPropertyName("row_offset")]
    public long RowOffset { get; init; }

    [JsonPropertyName("row_count")]
    public long RowCount { get; init; }

    [JsonPropertyName("next_chunk_index")]
    public int? NextChunkIndex { get; init; }

    [JsonPropertyName("next_chunk_internal_link")]
    public string? NextChunkInternalLink { get; init; }

    /// <summary>Inline JSON rows (JSON_ARRAY format): array of rows, each an array of nullable strings.</summary>
    [JsonPropertyName("data_array")]
    public IReadOnlyList<IReadOnlyList<string?>>? DataArray { get; init; }

    /// <summary>Inline Arrow IPC stream, base64-encoded (ARROW_STREAM + INLINE, when supported).</summary>
    [JsonPropertyName("attachment")]
    public string? Attachment { get; init; }

    [JsonPropertyName("external_links")]
    public IReadOnlyList<ExternalLink>? ExternalLinks { get; init; }

    /// <summary>
    /// A live Arrow record-batch stream supplied directly by a streaming transport
    /// (e.g. the Thrift add-on). Never populated from JSON. When set, the reader drains
    /// this stream instead of fetching chunks, and disposes it when done.
    /// </summary>
    [JsonIgnore]
    public Apache.Arrow.Ipc.IArrowArrayStream? ArrowStream { get; init; }
}

/// <summary>A presigned URL from which one result chunk can be downloaded.</summary>
public sealed class ExternalLink
{
    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; init; }

    [JsonPropertyName("row_offset")]
    public long RowOffset { get; init; }

    [JsonPropertyName("row_count")]
    public long RowCount { get; init; }

    [JsonPropertyName("byte_count")]
    public long ByteCount { get; init; }

    [JsonPropertyName("external_link")]
    public string? Link { get; init; }

    [JsonPropertyName("expiration")]
    public DateTimeOffset? Expiration { get; init; }

    [JsonPropertyName("next_chunk_index")]
    public int? NextChunkIndex { get; init; }
}
