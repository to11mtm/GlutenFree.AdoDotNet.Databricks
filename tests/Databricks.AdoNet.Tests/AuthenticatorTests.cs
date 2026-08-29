using System.Net;
using Databricks.AdoNet.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Databricks.AdoNet.Tests;

public class AuthenticatorTests
{
    private const string Host = "https://adb-1.azuredatabricks.net";

    [Fact]
    public async Task Pat_authenticator_returns_token()
    {
        var auth = new PatAuthenticator("dapi123");
        Assert.Equal("dapi123", await auth.GetTokenAsync());
    }

    [Fact]
    public void Pat_authenticator_rejects_empty_token()
    {
        Assert.Throws<ArgumentException>(() => new PatAuthenticator(""));
    }

    [Fact]
    public async Task OAuth_requests_token_with_client_credentials()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"access_token":"tok1","expires_in":3600}""");
        using var auth = CreateOAuth(handler);

        var token = await auth.GetTokenAsync();

        Assert.Equal("tok1", token);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal($"{Host}/oidc/v1/token", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("scope=all-apis", body);
    }

    [Fact]
    public async Task OAuth_caches_token_until_near_expiry()
    {
        var time = new FakeTimeProvider();
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"access_token":"tok1","expires_in":3600}""")
            .Enqueue(HttpStatusCode.OK, """{"access_token":"tok2","expires_in":3600}""");
        using var auth = CreateOAuth(handler, time);

        Assert.Equal("tok1", await auth.GetTokenAsync());
        Assert.Equal("tok1", await auth.GetTokenAsync());
        Assert.Single(handler.Requests);

        // Advance past expiry (minus the 60s early-refresh margin).
        time.Advance(TimeSpan.FromSeconds(3600 - 59));

        Assert.Equal("tok2", await auth.GetTokenAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task OAuth_failure_throws_DatabricksException_without_leaking_body()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.Unauthorized, """{"error":"invalid_client","secret_echo":"shh"}""");
        using var auth = CreateOAuth(handler);

        var ex = await Assert.ThrowsAsync<DatabricksException>(async () => await auth.GetTokenAsync());

        Assert.Equal(401, ex.StatusCode);
        Assert.DoesNotContain("shh", ex.Message);
    }

    [Fact]
    public async Task OAuth_missing_access_token_throws()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"expires_in":3600}""");
        using var auth = CreateOAuth(handler);

        await Assert.ThrowsAsync<DatabricksException>(async () => await auth.GetTokenAsync());
    }

    [Fact]
    public async Task OAuth_concurrent_callers_share_single_request()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"access_token":"tok1","expires_in":3600}""");
        using var auth = CreateOAuth(handler);

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => auth.GetTokenAsync().AsTask()));

        Assert.All(tokens, t => Assert.Equal("tok1", t));
        Assert.Single(handler.Requests);
    }

    private static OAuthM2MAuthenticator CreateOAuth(FakeHttpHandler handler, TimeProvider? time = null)
        => new(Host, "client-id", "client-secret", new HttpClient(handler), time);
}
