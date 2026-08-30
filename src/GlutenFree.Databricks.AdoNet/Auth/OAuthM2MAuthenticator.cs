using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlutenFree.Databricks.AdoNet.Auth;

/// <summary>
/// Authenticates using OAuth machine-to-machine (service principal) client credentials
/// against the workspace token endpoint (<c>/oidc/v1/token</c>). Tokens are cached and
/// refreshed shortly before expiry; acquisition is single-flighted across threads.
/// </summary>
public sealed class OAuthM2MAuthenticator : IDatabricksAuthenticator, IDisposable
{
    private static readonly TimeSpan s_expiryMargin = TimeSpan.FromSeconds(60);

    private readonly Uri _tokenEndpoint;
    private readonly string _basicCredentials;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Immutable token+expiry snapshot published through a single volatile reference so the
    // lock-free fast path can never pair an old token with a newly written expiry (or vice versa).
    private CachedToken? _cached;

    private sealed record CachedToken(string Token, long ExpiresAtTicks);

    /// <summary>
    /// Creates an authenticator for the given workspace host and service principal credentials.
    /// </summary>
    /// <param name="host">Workspace base URL (e.g. <c>https://adb-123.azuredatabricks.net</c>).</param>
    /// <param name="clientId">Service principal application id.</param>
    /// <param name="clientSecret">Service principal OAuth secret.</param>
    /// <param name="httpClient">Optional HTTP client (owned by the caller); one is created if omitted.</param>
    /// <param name="timeProvider">Optional clock, for testing.</param>
    public OAuthM2MAuthenticator(
        string host,
        string clientId,
        string clientSecret,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(clientSecret);

        _tokenEndpoint = new Uri(new Uri(host, UriKind.Absolute), "/oidc/v1/token");
        if (_tokenEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The workspace host must use https; OAuth client credentials must never be sent over plaintext http.",
                nameof(host));
        }

        _basicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        _httpClient = httpClient ?? DatabricksSharedResources.HttpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (IsValid(cached))
        {
            return cached.Token;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited.
            cached = Volatile.Read(ref _cached);
            if (IsValid(cached))
            {
                return cached.Token;
            }

            var (token, expiresIn) = await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            Publish(token, expiresIn);
            return token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public string GetToken(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (IsValid(cached))
        {
            return cached.Token;
        }

        _refreshLock.Wait(cancellationToken);
        try
        {
            cached = Volatile.Read(ref _cached);
            if (IsValid(cached))
            {
                return cached.Token;
            }

            using var request = CreateTokenRequest();
            // Genuinely synchronous I/O: no sync-over-async blocking.
            using var response = _httpClient.Send(request, cancellationToken);
            using var bodyReader = new StreamReader(response.Content.ReadAsStream(cancellationToken));
            var (token, expiresIn) = ParseTokenResponse(response, bodyReader.ReadToEnd());

            Publish(token, expiresIn);
            return token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsValid([NotNullWhen(true)] CachedToken? cached)
        => cached is not null && _timeProvider.GetUtcNow().UtcTicks < cached.ExpiresAtTicks;

    private void Publish(string token, TimeSpan expiresIn)
        => Volatile.Write(
            ref _cached,
            new CachedToken(token, (_timeProvider.GetUtcNow() + expiresIn - s_expiryMargin).UtcTicks));

    private async Task<(string Token, TimeSpan ExpiresIn)> RequestTokenAsync(CancellationToken cancellationToken)
    {
        using var request = CreateTokenRequest();
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseTokenResponse(response, body);
    }

    private HttpRequestMessage CreateTokenRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "all-apis",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicCredentials);
        return request;
    }

    private (string Token, TimeSpan ExpiresIn) ParseTokenResponse(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Do not include the response body verbatim in case it echoes credentials;
            // include the status code and endpoint only.
            throw new DatabricksException(
                $"OAuth token request to '{_tokenEndpoint}' failed with status {(int)response.StatusCode} ({response.StatusCode}).",
                (int)response.StatusCode);
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var token = root.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new DatabricksException("OAuth token response did not contain an access_token.");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(60);

        return (token, expiresIn);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The HttpClient is either the process-wide shared client or caller-owned;
        // the authenticator never disposes it.
        _refreshLock.Dispose();
    }
}
