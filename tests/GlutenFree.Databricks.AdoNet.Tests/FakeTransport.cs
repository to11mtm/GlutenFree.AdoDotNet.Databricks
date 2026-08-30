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

    public Task<StatementResponse> ExecuteStatementAsync(
        StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        ExecutedRequests.Add(request);
        return Task.FromResult(NextResponse ?? throw new InvalidOperationException("NextResponse not set."));
    }

    public Task<ResultData> GetResultChunkAsync(
        string statementId, int chunkIndex, CancellationToken cancellationToken)
    {
        ChunkRequests.Add((statementId, chunkIndex));
        return Task.FromResult(Chunks[chunkIndex]);
    }

    public Task<byte[]> DownloadExternalLinkAsync(ExternalLink link, CancellationToken cancellationToken)
        => Task.FromResult(ExternalLinkData[link.Link!]);

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
}
