namespace GlutenFree.Databricks.AdoNet.Transport;

/// <summary>
/// Abstraction over the wire protocol used to execute statements against Databricks.
/// The initial implementation is <see cref="RestStatementTransport"/> (Statement Execution API);
/// a Thrift/HiveServer2 transport may be added later behind this same interface.
/// </summary>
public interface IDatabricksTransport : IAsyncDisposable
{
    /// <summary>
    /// Submits a statement and waits (server-side hybrid wait plus client-side polling)
    /// until it reaches a terminal or result-ready state.
    /// Throws <see cref="DatabricksException"/> for FAILED/CANCELED/CLOSED statements.
    /// </summary>
    Task<StatementResponse> ExecuteStatementAsync(
        StatementRequest request,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken);

    /// <summary>Fetches chunk metadata/links for a chunk of a statement's result.</summary>
    Task<ResultData> GetResultChunkAsync(
        string statementId,
        int chunkIndex,
        CancellationToken cancellationToken);

    /// <summary>Downloads the raw bytes of an external result link (presigned URL).</summary>
    Task<byte[]> DownloadExternalLinkAsync(ExternalLink link, CancellationToken cancellationToken);

    /// <summary>Requests cancellation of a running statement. Best-effort; does not throw on failure.</summary>
    Task CancelStatementAsync(string statementId, CancellationToken cancellationToken);
}
