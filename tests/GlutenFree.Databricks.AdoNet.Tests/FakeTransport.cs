using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>In-memory IDatabricksTransport for command/reader tests.</summary>
public sealed class FakeTransport : IDatabricksTransport
{
    public List<StatementRequest> ExecutedRequests { get; } = [];
    public List<string> CanceledStatements { get; } = [];
    public List<(string StatementId, int ChunkIndex)> ChunkRequests { get; } = [];

    public StatementResponse? NextResponse { get; set; }
    public Dictionary<int, ResultData> Chunks { get; } = [];
    public Dictionary<string, byte[]> ExternalLinkData { get; } = [];
    public bool Disposed { get; private set; }

    /// <summary>Emulates a session-capable transport (the Thrift transport) when set.</summary>
    public bool SupportsTransactions { get; set; }

    /// <summary>The statement texts executed so far, in order.</summary>
    public IEnumerable<string> ExecutedSql => ExecutedRequests.Select(r => r.Statement);

    /// <summary>True when teardown went through the synchronous <see cref="Dispose"/> path.</summary>
    public bool DisposedSynchronously { get; private set; }

    public Task<StatementResponse> ExecuteStatementAsync(
        StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        ExecutedRequests.Add(request);
        return Task.FromResult(NextResponse ?? throw new InvalidOperationException("NextResponse not set."));
    }

    public StatementResponse ExecuteStatement(
        StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        ExecutedRequests.Add(request);
        return NextResponse ?? throw new InvalidOperationException("NextResponse not set.");
    }

    public Task<ResultData> GetResultChunkAsync(
        string statementId, int chunkIndex, CancellationToken cancellationToken)
    {
        ChunkRequests.Add((statementId, chunkIndex));
        return Task.FromResult(Chunks[chunkIndex]);
    }

    public ResultData GetResultChunk(string statementId, int chunkIndex, CancellationToken cancellationToken)
    {
        ChunkRequests.Add((statementId, chunkIndex));
        return Chunks[chunkIndex];
    }

    public Task<byte[]> DownloadExternalLinkAsync(ExternalLink link, CancellationToken cancellationToken)
        => Task.FromResult(ExternalLinkData[link.Link!]);

    public byte[] DownloadExternalLink(ExternalLink link, CancellationToken cancellationToken)
        => ExternalLinkData[link.Link!];

    public Task CancelStatementAsync(string statementId, CancellationToken cancellationToken)
    {
        CanceledStatements.Add(statementId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
        DisposedSynchronously = true;
    }
}
