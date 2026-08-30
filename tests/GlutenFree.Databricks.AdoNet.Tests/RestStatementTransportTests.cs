using System.Net;
using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.Auth;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

public class RestStatementTransportTests
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

    private static RestStatementTransport CreateTransport(
        FakeHttpHandler handler, int maxRetries = 4)
        => new(
            Host,
            new PatAuthenticator("dapi123"),
            new HttpClient(handler),
            maxRetries,
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

    private static StatementRequest CreateRequest() => new()
    {
        Statement = "SELECT 1",
        WarehouseId = "wh1",
        Format = "JSON_ARRAY",
        Disposition = "INLINE",
    };

    [Fact]
    public async Task Execute_submits_statement_and_returns_immediate_success()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, SucceededResponse);
        await using var transport = CreateTransport(handler);

        var response = await transport.ExecuteStatementAsync(
            CreateRequest(), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("stmt-1", response.StatementId);
        Assert.Equal("SUCCEEDED", response.Status!.State);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{Host}/api/2.0/sql/statements", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("dapi123", request.Headers.Authorization.Parameter);
        Assert.Contains("\"statement\":\"SELECT 1\"", body);
        Assert.Contains("\"warehouse_id\":\"wh1\"", body);
        Assert.Contains("\"wait_timeout\":\"30s\"", body);
        Assert.Contains("\"on_wait_timeout\":\"CONTINUE\"", body);
    }

    [Fact]
    public async Task Execute_polls_until_statement_succeeds()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"statement_id":"stmt-1","status":{"state":"PENDING"}}""")
            .Enqueue(HttpStatusCode.OK, """{"statement_id":"stmt-1","status":{"state":"RUNNING"}}""")
            .Enqueue(HttpStatusCode.OK, SucceededResponse);
        await using var transport = CreateTransport(handler);

        var response = await transport.ExecuteStatementAsync(
            CreateRequest(), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("SUCCEEDED", response.Status!.State);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Request.Method);
        Assert.EndsWith("/api/2.0/sql/statements/stmt-1", handler.Requests[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Execute_throws_for_failed_statement_with_error_details()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {
              "statement_id": "stmt-9",
              "status": {
                "state": "FAILED",
                "error": { "error_code": "BAD_REQUEST", "message": "TABLE_OR_VIEW_NOT_FOUND: nope" }
              }
            }
            """);
        await using var transport = CreateTransport(handler);

        var ex = await Assert.ThrowsAsync<DatabricksException>(
            () => transport.ExecuteStatementAsync(CreateRequest(), TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("stmt-9", ex.StatementId);
        Assert.Equal("BAD_REQUEST", ex.DatabricksErrorCode);
        Assert.Contains("TABLE_OR_VIEW_NOT_FOUND", ex.Message);
    }

    [Fact]
    public async Task Execute_retries_on_429_then_succeeds()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, """{"error_code":"RESOURCE_EXHAUSTED"}""")
            .Enqueue(HttpStatusCode.OK, SucceededResponse);
        await using var transport = CreateTransport(handler);

        var response = await transport.ExecuteStatementAsync(
            CreateRequest(), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("SUCCEEDED", response.Status!.State);
        Assert.Equal(2, handler.Requests.Count);
        // Retried request must carry the same body.
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task Execute_gives_up_after_max_retries()
    {
        var handler = new FakeHttpHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.Enqueue(HttpStatusCode.TooManyRequests, """{"error_code":"RESOURCE_EXHAUSTED"}""");
        }

        await using var transport = CreateTransport(handler, maxRetries: 2);

        var ex = await Assert.ThrowsAsync<DatabricksException>(
            () => transport.ExecuteStatementAsync(CreateRequest(), TimeSpan.Zero, CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("RESOURCE_EXHAUSTED", ex.DatabricksErrorCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Execute_does_not_retry_submission_on_503()
    {
        // A 503 can be returned after the server accepted the POST; transparently
        // resending the statement could execute DML twice, so submission must fail fast.
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, """{"error_code":"TEMPORARILY_UNAVAILABLE"}""")
            .Enqueue(HttpStatusCode.OK, SucceededResponse);
        await using var transport = CreateTransport(handler);

        var ex = await Assert.ThrowsAsync<DatabricksException>(
            () => transport.ExecuteStatementAsync(CreateRequest(), TimeSpan.Zero, CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Execute_retries_status_poll_on_503()
    {
        // Polling GETs are idempotent, so 503s there stay transparently retryable.
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"statement_id":"stmt-1","status":{"state":"PENDING"}}""")
            .Enqueue(HttpStatusCode.ServiceUnavailable, """{"error_code":"TEMPORARILY_UNAVAILABLE"}""")
            .Enqueue(HttpStatusCode.OK, SucceededResponse);
        await using var transport = CreateTransport(handler);

        var response = await transport.ExecuteStatementAsync(
            CreateRequest(), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("SUCCEEDED", response.Status!.State);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Request.Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Request.Method);
    }

    [Fact]
    public async Task Execute_cancellation_during_polling_cancels_statement()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpHandler()
            .Enqueue(request =>
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(50));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"statement_id":"stmt-1","status":{"state":"PENDING"}}""",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            })
            .Enqueue(HttpStatusCode.OK, "{}"); // cancel call
        await using var transport = CreateTransport(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExecuteStatementAsync(CreateRequest(), TimeSpan.Zero, cts.Token));

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/statements/stmt-1/cancel", handler.Requests[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetResultChunk_parses_external_links()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {
              "chunk_index": 1,
              "row_offset": 100,
              "row_count": 50,
              "external_links": [
                { "chunk_index": 1, "external_link": "https://storage.example/xyz?sig=abc", "byte_count": 1024 }
              ]
            }
            """);
        await using var transport = CreateTransport(handler);

        var chunk = await transport.GetResultChunkAsync("stmt-1", 1, CancellationToken.None);

        Assert.Equal(1, chunk.ChunkIndex);
        var link = Assert.Single(chunk.ExternalLinks!);
        Assert.Equal("https://storage.example/xyz?sig=abc", link.Link);
        Assert.EndsWith("/statements/stmt-1/result/chunks/1", handler.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadExternalLink_does_not_send_bearer_token()
    {
        var payload = new byte[] { 1, 2, 3 };
        var handler = new FakeHttpHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        await using var transport = CreateTransport(handler);

        var bytes = await transport.DownloadExternalLinkAsync(
            new ExternalLink { ChunkIndex = 0, Link = "https://storage.example/data?sig=abc" },
            CancellationToken.None);

        Assert.Equal(payload, bytes);
        Assert.Null(handler.Requests[0].Request.Headers.Authorization);
    }

    [Fact]
    public async Task Cancel_swallows_transport_errors()
    {
        var handler = new FakeHttpHandler().Enqueue(_ => throw new HttpRequestException("boom"));
        await using var transport = CreateTransport(handler);

        await transport.CancelStatementAsync("stmt-1", CancellationToken.None);
    }
}
