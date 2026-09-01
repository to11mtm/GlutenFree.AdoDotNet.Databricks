using System.Net;
using GlutenFree.Databricks.AdoNet.Auth;
using GlutenFree.Databricks.AdoNet.Internal;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>
/// Verifies the genuinely-synchronous code paths (HttpClient.Send-based) used by the
/// sync ADO.NET surface: no sync-over-async blocking anywhere in the pipeline.
/// </summary>
public class SyncPathTests
{
    private const string Host = "https://adb-1.azuredatabricks.net";

    private static readonly string SucceededResponse = """
        {
          "statement_id": "stmt-1",
          "status": { "state": "SUCCEEDED" },
          "manifest": {
            "format": "JSON_ARRAY",
            "total_chunk_count": 1,
            "total_row_count": 1,
            "schema": { "column_count": 1, "columns": [ { "name": "a", "type_name": "INT", "position": 0 } ] }
          },
          "result": { "chunk_index": 0, "row_count": 1, "data_array": [["1"]] }
        }
        """;

    private static RestStatementTransport CreateTransport(FakeHttpHandler handler)
        => new(
            Host,
            new PatAuthenticator("dapi123"),
            new HttpClient(handler),
            maxRetries: 2,
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

    private static StatementRequest CreateRequest() => new()
    {
        Statement = "SELECT 1",
        WarehouseId = "wh1",
        Format = "JSON_ARRAY",
        Disposition = "INLINE",
    };

    [Fact]
    public void Transport_ExecuteStatement_sync_submits_and_polls()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"statement_id":"stmt-1","status":{"state":"PENDING"}}""")
            .Enqueue(HttpStatusCode.OK, SucceededResponse);
        using var httpTransport = CreateTransport(handler);

        var response = ((IDatabricksTransport)httpTransport).ExecuteStatement(
            CreateRequest(), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("SUCCEEDED", response.Status!.State);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Transport_sync_retries_on_429()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, """{"error_code":"RESOURCE_EXHAUSTED"}""")
            .Enqueue(HttpStatusCode.OK, SucceededResponse);
        using var httpTransport = CreateTransport(handler);

        var response = ((IDatabricksTransport)httpTransport).ExecuteStatement(
            CreateRequest(), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("SUCCEEDED", response.Status!.State);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public void Transport_sync_failure_throws_DatabricksException()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"statement_id":"stmt-9","status":{"state":"FAILED","error":{"error_code":"BAD_REQUEST","message":"nope"}}}
            """);
        using var httpTransport = CreateTransport(handler);

        var ex = Assert.Throws<DatabricksException>(
            () => ((IDatabricksTransport)httpTransport).ExecuteStatement(
                CreateRequest(), TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("BAD_REQUEST", ex.DatabricksErrorCode);
    }

    [Fact]
    public void Sync_command_and_reader_work_end_to_end_via_fake_transport()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        connection.Open(); // sync open
        transport.NextResponse = new StatementResponse
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "JSON_ARRAY",
                TotalChunkCount = 2,
                TotalRowCount = 2,
                Schema = new ResultSchema
                {
                    ColumnCount = 1,
                    Columns = [new ColumnInfo { Name = "v", TypeName = "INT", Position = 0 }],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [["1"]] },
        };
        transport.Chunks[1] = new ResultData { ChunkIndex = 1, RowCount = 1, DataArray = [["2"]] };

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t";
        using var reader = command.ExecuteReader(); // sync execute

        var values = new List<int>();
        while (reader.Read()) // sync read incl. sync chunk fetch
        {
            values.Add(reader.GetInt32(0));
        }

        Assert.Equal([1, 2], values);
        connection.Close();
    }

    [Fact]
    public void Sync_OAuth_token_acquisition_uses_sync_http()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"access_token":"tok1","expires_in":3600}""");
        using var auth = new OAuthM2MAuthenticator(Host, "id", "secret", new HttpClient(handler));

        var token = auth.GetToken();

        Assert.Equal("tok1", token);
        // Cached on subsequent calls (sync and async share the cache).
        Assert.Equal("tok1", auth.GetToken());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Close_disposes_the_transport_synchronously()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        connection.Open();

        connection.Close();

        Assert.True(transport.DisposedSynchronously);
    }

    [Fact]
    public void Dispose_disposes_the_transport_synchronously()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        connection.Open();

        connection.Dispose();

        Assert.True(transport.DisposedSynchronously);
    }

    [Fact]
    public async Task SyncOverAsync_does_not_deadlock_under_a_blocking_SynchronizationContext()
    {
        Func<Task<int>> work = async () =>
        {
            // No ConfigureAwait(false): would resume on the caller's context if run inline.
            await Task.Yield();
            return 42;
        };

        var worker = Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedSynchronizationContext());
            try
            {
                return SyncOverAsync.Run(work);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });

        var finished = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(
            ReferenceEquals(finished, worker),
            "SyncOverAsync.Run deadlocked against the caller's SynchronizationContext.");
        Assert.Equal(42, await worker);
    }

    [Fact]
    public void SyncOverAsync_propagates_the_original_exception_unwrapped()
    {
        Func<Task<int>> work = () => Task.FromException<int>(new InvalidOperationException("boom"));

        var ex = Assert.Throws<InvalidOperationException>(() => SyncOverAsync.Run(work));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void SyncOverAsync_supports_ValueTask_returning_work()
    {
        Func<ValueTask<int>> work = () => new ValueTask<int>(7);

        Assert.Equal(7, SyncOverAsync.Run(work));
    }

    /// <summary>
    /// Queues continuations that are never executed: any attempt to resume on this context
    /// while its thread is blocked deadlocks, which is exactly what the helper must avoid.
    /// </summary>
    private sealed class NeverPumpedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Intentionally dropped.
        }

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException();
    }
}
