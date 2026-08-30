using System.Data;
using System.Net;
using System.Net.Http.Headers;
using GlutenFree.Databricks.AdoNet.Auth;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>Tests for the PR-review hardening fixes: https enforcement, CloseConnection
/// behavior, TIMESTAMP_NTZ kind preservation, and Retry-After HTTP-date handling.</summary>
public class ReviewHardeningTests
{
    [Fact]
    public void ConnectionString_validation_rejects_http_host()
    {
        var builder = new DatabricksConnectionStringBuilder(
            "Host=http://adb-1.azuredatabricks.net;WarehouseId=w;Token=t");

        var ex = Assert.Throws<ArgumentException>(builder.Validate);
        Assert.Contains("https", ex.Message);
    }

    [Fact]
    public void OAuth_authenticator_rejects_http_host()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new OAuthM2MAuthenticator("http://adb-1.azuredatabricks.net", "id", "secret"));
        Assert.Contains("https", ex.Message);
    }

    [Fact]
    public void Transport_rejects_http_host()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new RestStatementTransport("http://adb-1.azuredatabricks.net", new PatAuthenticator("t")));
        Assert.Contains("https", ex.Message);
    }

    [Fact]
    public void CloseConnection_behavior_closes_connection_when_reader_closes()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        connection.Open();
        transport.NextResponse = Responses.EmptySuccess;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        var reader = command.ExecuteReader(CommandBehavior.CloseConnection);
        Assert.Equal(ConnectionState.Open, connection.State);

        reader.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Default_behavior_leaves_connection_open_when_reader_closes()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        await connection.OpenAsync();
        transport.NextResponse = Responses.EmptySuccess;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        var reader = await command.ExecuteReaderAsync();
        await reader.DisposeAsync();

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public void Timestamp_ntz_json_preserves_unspecified_kind()
    {
        var ntz = (DateTime)DatabricksTypeMap.ConvertJsonValue(
            "2026-08-29 12:34:56.789", new ColumnInfo { Name = "t", TypeName = "TIMESTAMP_NTZ" });
        var utc = (DateTime)DatabricksTypeMap.ConvertJsonValue(
            "2026-08-29T12:34:56.789Z", new ColumnInfo { Name = "t", TypeName = "TIMESTAMP" });

        Assert.Equal(DateTimeKind.Unspecified, ntz.Kind);
        Assert.Equal(new DateTime(2026, 8, 29, 12, 34, 56, 789), ntz);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void Timestamp_ntz_roundtrips_back_as_ntz_parameter()
    {
        // Read NTZ -> Unspecified kind -> rebinding infers TIMESTAMP_NTZ, not TIMESTAMP.
        var value = (DateTime)DatabricksTypeMap.ConvertJsonValue(
            "2026-08-29 12:34:56", new ColumnInfo { Name = "t", TypeName = "TIMESTAMP_NTZ" });

        var wire = new DatabricksParameter("p", value).ToStatementParameter();

        Assert.Equal("TIMESTAMP_NTZ", wire.Type);
    }

    [Fact]
    public async Task Retry_after_http_date_is_honored()
    {
        var succeeded = """
            {
              "statement_id": "stmt-1",
              "status": { "state": "SUCCEEDED" },
              "manifest": { "format": "JSON_ARRAY", "total_chunk_count": 0, "total_row_count": 0,
                            "schema": { "column_count": 0, "columns": [] } }
            }
            """;
        var handler = new FakeHttpHandler()
            .Enqueue(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                };
                // HTTP-date form (no delta); a past date must clamp to zero delay.
                response.Headers.RetryAfter = new RetryConditionHeaderValue(
                    DateTimeOffset.UtcNow.AddMilliseconds(50));
                return response;
            })
            .Enqueue(HttpStatusCode.OK, succeeded);
        await using var transport = new RestStatementTransport(
            "https://adb-1.azuredatabricks.net",
            new PatAuthenticator("t"),
            new HttpClient(handler),
            maxRetries: 2,
            retryBaseDelay: TimeSpan.FromMinutes(5)); // would time the test out if backoff were used

        var response = await transport.ExecuteStatementAsync(
            new StatementRequest { Statement = "SELECT 1", WarehouseId = "w" },
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal("SUCCEEDED", response.Status!.State);
        Assert.Equal(2, handler.Requests.Count);
    }
}
