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
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _cachedToken;

    // UTC ticks; read/written atomically (long + Volatile) so the lock-free fast path
    // never sees a torn DateTimeOffset value.
    private long _expiresAtTicks = long.MinValue;

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
        _basicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cachedToken);
        if (cached is not null && IsTokenValid)
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited.
            cached = _cachedToken;
            if (cached is not null && IsTokenValid)
            {
                return cached;
            }

            var (token, expiresIn) = await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            SetExpiry(expiresIn);
            Volatile.Write(ref _cachedToken, token);
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
        var cached = Volatile.Read(ref _cachedToken);
        if (cached is not null && IsTokenValid)
        {
            return cached;
        }

        _refreshLock.Wait(cancellationToken);
        try
        {
            cached = _cachedToken;
            if (cached is not null && IsTokenValid)
            {
                return cached;
            }

            using var request = CreateTokenRequest();
            // Genuinely synchronous I/O: no sync-over-async blocking.
            using var response = _httpClient.Send(request, cancellationToken);
            using var bodyReader = new StreamReader(response.Content.ReadAsStream(cancellationToken));
            var (token, expiresIn) = ParseTokenResponse(response, bodyReader.ReadToEnd());

            SetExpiry(expiresIn);
            Volatile.Write(ref _cachedToken, token);
            return token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsTokenValid
        => _timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _expiresAtTicks);

    private void SetExpiry(TimeSpan expiresIn)
        => Volatile.Write(ref _expiresAtTicks, (_timeProvider.GetUtcNow() + expiresIn - s_expiryMargin).UtcTicks);

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
        _refreshLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
