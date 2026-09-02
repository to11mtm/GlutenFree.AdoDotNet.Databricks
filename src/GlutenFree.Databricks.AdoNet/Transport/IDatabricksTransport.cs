namespace GlutenFree.Databricks.AdoNet.Transport;

/// <summary>
/// Abstraction over the wire protocol used to execute statements against Databricks.
/// The initial implementation is <see cref="RestStatementTransport"/> (Statement Execution API);
/// a Thrift/HiveServer2 transport may be added later behind this same interface.
/// </summary>
/// <remarks>
/// Every asynchronous member has a synchronous counterpart that implementations must provide
/// explicitly: there are deliberately no default (sync-over-async) implementations, so a
/// transport can never silently block a caller's thread on async I/O. Transports with no
/// synchronous protocol support should throw <see cref="NotSupportedException"/> instead.
/// </remarks>
public interface IDatabricksTransport : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// True when the transport maintains a server-side session that can hold interactive
    /// transaction state (<c>BEGIN TRANSACTION</c> / <c>COMMIT</c> / <c>ROLLBACK</c>).
    /// </summary>
    /// <remarks>
    /// Interactive transactions are session state, so a stateless transport (the REST
    /// Statement Execution API) cannot support them and returns <see langword="false"/>;
    /// callers there must use a self-contained <c>BEGIN ATOMIC ... END;</c> block instead.
    /// The default is the conservative <see langword="false"/> so that transports written
    /// against an earlier version of this interface keep compiling and behaving correctly.
    /// </remarks>
    bool SupportsTransactions => false;

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

    /// <summary>
    /// Synchronous counterpart of <see cref="ExecuteStatementAsync"/>. Implementations must use
    /// genuinely synchronous I/O (or throw <see cref="NotSupportedException"/>); blocking on the
    /// async path is not an acceptable implementation.
    /// </summary>
    StatementResponse ExecuteStatement(
        StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken);

    /// <summary>Synchronous counterpart of <see cref="GetResultChunkAsync"/>.</summary>
    ResultData GetResultChunk(string statementId, int chunkIndex, CancellationToken cancellationToken);

    /// <summary>Synchronous counterpart of <see cref="DownloadExternalLinkAsync"/>.</summary>
    byte[] DownloadExternalLink(ExternalLink link, CancellationToken cancellationToken);
}
