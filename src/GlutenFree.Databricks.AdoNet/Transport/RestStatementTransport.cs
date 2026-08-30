using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GlutenFree.Databricks.AdoNet.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlutenFree.Databricks.AdoNet.Transport;

/// <summary>
/// <see cref="IDatabricksTransport"/> implementation backed by the Databricks
/// SQL Statement Execution API (<c>/api/2.0/sql/statements</c>).
/// </summary>
public sealed class RestStatementTransport : IDatabricksTransport, IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan s_maxServerWait = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan s_minServerWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_maxPollDelay = TimeSpan.FromSeconds(5);

    private readonly Uri _baseUri;
    private readonly IDatabricksAuthenticator _authenticator;
    private readonly HttpClient _httpClient;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBaseDelay;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a transport bound to a workspace.</summary>
    /// <param name="host">Workspace base URL.</param>
    /// <param name="authenticator">Bearer token source.</param>
    /// <param name="httpClient">
    /// Optional HTTP client (caller-owned; never disposed by the transport). The process-wide
    /// shared client is used if omitted, so connections share one handler/connection pool.
    /// </param>
    /// <param name="maxRetries">Maximum retries for 429/503 responses.</param>
    /// <param name="retryBaseDelay">Base delay for exponential backoff.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="timeProvider">Optional clock, for testing.</param>
    public RestStatementTransport(
        string host,
        IDatabricksAuthenticator authenticator,
        HttpClient? httpClient = null,
        int maxRetries = 4,
        TimeSpan? retryBaseDelay = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentNullException.ThrowIfNull(authenticator);

        _baseUri = new Uri(host, UriKind.Absolute);
        if (_baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The workspace host must use https; bearer tokens must never be sent over plaintext http.",
                nameof(host));
        }

        _authenticator = authenticator;
        _httpClient = httpClient ?? DatabricksSharedResources.HttpClient;
        _maxRetries = maxRetries;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(500);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RestStatementTransport>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<StatementResponse> ExecuteStatementAsync(
        StatementRequest request,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeoutCts = commandTimeout > TimeSpan.Zero
            ? new CancellationTokenSource(commandTimeout, _timeProvider)
            : null;
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        // Ask the server to wait synchronously for a bounded time before we fall back to polling.
        var submitRequest = BuildSubmitRequest(request, commandTimeout);

        StatementResponse response;
        try
        {
            // Submission is not idempotent: a 503 can arrive after the server accepted the
            // POST, so transparently resending could execute DML twice (idempotent: false
            // restricts retries to 429, which is rejected before execution).
            response = await SendJsonAsync<StatementResponse>(
                    () => new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "/api/2.0/sql/statements"))
                    {
                        Content = JsonContent.Create(submitRequest, options: s_jsonOptions),
                    },
                    token,
                    idempotent: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            throw new DatabricksException($"Statement submission timed out after {commandTimeout}.");
        }

        var statementId = response.StatementId
            ?? throw new DatabricksException("Statement submission response did not include a statement_id.");

        var pollDelay = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            if (CheckStatementState(statementId, response))
            {
                return response;
            }

            try
            {
                await Task.Delay(pollDelay, _timeProvider, token).ConfigureAwait(false);
                response = await SendJsonAsync<StatementResponse>(
                        () => new HttpRequestMessage(HttpMethod.Get, StatementUri(statementId)),
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeoutCts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
            {
                await CancelStatementAsync(statementId, CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new DatabricksException(
                    $"Statement '{statementId}' timed out after {commandTimeout} and was canceled.")
                {
                    StatementId = statementId,
                };
            }

            pollDelay = pollDelay >= s_maxPollDelay ? s_maxPollDelay : pollDelay * 2;
        }
    }

    /// <inheritdoc />
    public Task<ResultData> GetResultChunkAsync(
        string statementId, int chunkIndex, CancellationToken cancellationToken)
        => SendJsonAsync<ResultData>(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(_baseUri, $"/api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}/result/chunks/{chunkIndex}")),
            cancellationToken);

    /// <inheritdoc />
    public async Task<byte[]> DownloadExternalLinkAsync(ExternalLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrEmpty(link.Link))
        {
            throw new DatabricksException("External link did not contain a URL.");
        }

        // Presigned URLs carry their own authorization; never attach the workspace bearer token.
        using var request = new HttpRequestMessage(HttpMethod.Get, link.Link);
        using var response = await SendWithRetryAsync(request, authenticate: false, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DatabricksException(
                $"Downloading result chunk {link.ChunkIndex} failed with status {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Genuinely synchronous implementation using <see cref="HttpClient.Send(HttpRequestMessage, CancellationToken)"/>.</remarks>
    public StatementResponse ExecuteStatement(
        StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeoutCts = commandTimeout > TimeSpan.Zero
            ? new CancellationTokenSource(commandTimeout, _timeProvider)
            : null;
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        var submitRequest = BuildSubmitRequest(request, commandTimeout);

        StatementResponse response;
        try
        {
            // Submission is not idempotent — see the async path for why 503 is not retried.
            response = SendJson<StatementResponse>(
                () => new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "/api/2.0/sql/statements"))
                {
                    Content = JsonContent.Create(submitRequest, options: s_jsonOptions),
                },
                token,
                idempotent: false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            throw new DatabricksException($"Statement submission timed out after {commandTimeout}.");
        }

        var statementId = response.StatementId
            ?? throw new DatabricksException("Statement submission response did not include a statement_id.");

        var pollDelay = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            if (CheckStatementState(statementId, response))
            {
                return response;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                // Cancellation-aware synchronous wait (Thread.Sleep would block Cancel()).
                if (token.WaitHandle.WaitOne(pollDelay))
                {
                    token.ThrowIfCancellationRequested();
                }

                response = SendJson<StatementResponse>(
                    () => new HttpRequestMessage(HttpMethod.Get, StatementUri(statementId)),
                    token);
            }
            catch (OperationCanceledException) when (
                timeoutCts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
            {
                CancelStatement(statementId);
                cancellationToken.ThrowIfCancellationRequested();
                throw new DatabricksException(
                    $"Statement '{statementId}' timed out after {commandTimeout} and was canceled.")
                {
                    StatementId = statementId,
                };
            }

            pollDelay = pollDelay >= s_maxPollDelay ? s_maxPollDelay : pollDelay * 2;
        }
    }

    /// <inheritdoc />
    public ResultData GetResultChunk(string statementId, int chunkIndex, CancellationToken cancellationToken)
        => SendJson<ResultData>(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(_baseUri, $"/api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}/result/chunks/{chunkIndex}")),
            cancellationToken);

    /// <inheritdoc />
    public byte[] DownloadExternalLink(ExternalLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrEmpty(link.Link))
        {
            throw new DatabricksException("External link did not contain a URL.");
        }

        // Presigned URLs carry their own authorization; never attach the workspace bearer token.
        using var request = new HttpRequestMessage(HttpMethod.Get, link.Link);
        using var response = SendWithRetry(request, authenticate: false, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DatabricksException(
                $"Downloading result chunk {link.ChunkIndex} failed with status {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }

        using var memory = new MemoryStream();
        using var stream = response.Content.ReadAsStream(cancellationToken);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <inheritdoc />
    public async Task CancelStatementAsync(string statementId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_baseUri, $"/api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}/cancel"));
            using var response = await SendWithRetryAsync(request, authenticate: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Best-effort cancel of statement {StatementId} failed.", statementId);
        }
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="CancelStatementAsync"/> — genuinely synchronous
    /// (<see cref="SendWithRetry"/>) so the sync execution path never blocks on async work.
    /// Best-effort; does not throw on failure.
    /// </summary>
    private void CancelStatement(string statementId)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_baseUri, $"/api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}/cancel"));
            using var response = SendWithRetry(request, authenticate: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Best-effort cancel of statement {StatementId} failed.", statementId);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The HttpClient is either the process-wide shared client or caller-owned;
        // the transport never disposes it.
    }

    private Uri StatementUri(string statementId)
        => new(_baseUri, $"/api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}");

    private StatementRequest BuildSubmitRequest(StatementRequest request, TimeSpan commandTimeout)
    {
        // Ask the server to wait synchronously for a bounded time before we fall back to polling.
        var serverWait = commandTimeout > TimeSpan.Zero && commandTimeout < s_maxServerWait
            ? (commandTimeout < s_minServerWait ? s_minServerWait : commandTimeout)
            : TimeSpan.FromSeconds(30);
        return new StatementRequest
        {
            Statement = request.Statement,
            WarehouseId = request.WarehouseId,
            Catalog = request.Catalog,
            Schema = request.Schema,
            Parameters = request.Parameters,
            Format = request.Format,
            Disposition = request.Disposition,
            WaitTimeout = $"{(int)serverWait.TotalSeconds}s",
            OnWaitTimeout = "CONTINUE",
        };
    }

    /// <summary>
    /// Returns true when the statement succeeded, false when it is still pending/running,
    /// and throws for terminal failure states.
    /// </summary>
    private static bool CheckStatementState(string statementId, StatementResponse response)
        => response.Status?.State switch
        {
            "SUCCEEDED" => true,
            "PENDING" or "RUNNING" => false,
            "FAILED" or "CANCELED" or "CLOSED" => throw CreateStatementException(statementId, response.Status!),
            _ => throw new DatabricksException(
                $"Statement '{statementId}' reported unknown state '{response.Status?.State}'.")
            {
                StatementId = statementId,
            },
        };

    private T SendJson<T>(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken, bool idempotent = true)
    {
        using var request = requestFactory();
        using var response = SendWithRetry(request, authenticate: true, cancellationToken, idempotent);
        using var bodyReader = new StreamReader(response.Content.ReadAsStream(cancellationToken));
        var body = bodyReader.ReadToEnd();

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpException(response.StatusCode, body);
        }

        return JsonSerializer.Deserialize<T>(body, s_jsonOptions)
            ?? throw new DatabricksException("Databricks API returned an empty response body.");
    }

    private HttpResponseMessage SendWithRetry(
        HttpRequestMessage request, bool authenticate, CancellationToken cancellationToken, bool idempotent = true)
    {
        // Buffer the content up front and swap in a re-readable ByteArrayContent: after a
        // send, one-shot content streams are consumed and cannot be re-read for retry clones.
        byte[]? contentBytes = null;
        if (request.Content is not null)
        {
            using var memory = new MemoryStream();
            using (var stream = request.Content.ReadAsStream(cancellationToken))
            {
                stream.CopyTo(memory);
            }

            contentBytes = memory.ToArray();
            var buffered = new ByteArrayContent(contentBytes);
            foreach (var (key, values) in request.Content.Headers)
            {
                buffered.Headers.TryAddWithoutValidation(key, values);
            }

            request.Content = buffered;
        }

        // The caller owns (and disposes) the original request; this loop owns any clones it
        // creates, so the active clone is disposed in the finally block (safe even when its
        // response is returned — disposing a request message does not affect the response).
        var original = request;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (authenticate)
                {
                    var token = _authenticator.GetToken(cancellationToken);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                // Genuinely synchronous I/O: no sync-over-async blocking.
                var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!IsRetryable(response.StatusCode, idempotent) || attempt >= _maxRetries)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                _logger.LogDebug(
                    "Retrying request to {Uri} after {Delay} (attempt {Attempt}/{MaxRetries}, status {Status}).",
                    request.RequestUri, delay, attempt + 1, _maxRetries, (int)response.StatusCode);
                response.Dispose();

                cancellationToken.ThrowIfCancellationRequested();
                // Cancellation-aware synchronous wait: Cancel()/CommandTimeout must not be
                // forced to sit out a server-provided retry delay.
                if (cancellationToken.WaitHandle.WaitOne(delay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // HttpRequestMessage instances cannot be resent; clone for the retry
                // (synchronously — this is the genuinely-sync pipeline).
                var clone = CloneRequest(request, contentBytes);
                if (!ReferenceEquals(request, original))
                {
                    request.Dispose();
                }

                request = clone;
            }
        }
        finally
        {
            if (!ReferenceEquals(request, original))
            {
                request.Dispose();
            }
        }
    }

    private async Task<T> SendJsonAsync<T>(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken, bool idempotent = true)
    {
        using var request = requestFactory();
        using var response = await SendWithRetryAsync(request, authenticate: true, cancellationToken, idempotent)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpException(response.StatusCode, body);
        }

        return JsonSerializer.Deserialize<T>(body, s_jsonOptions)
            ?? throw new DatabricksException("Databricks API returned an empty response body.");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request, bool authenticate, CancellationToken cancellationToken, bool idempotent = true)
    {
        // Ownership mirrors the sync path: the caller disposes the original request, the loop
        // disposes any clones it creates (including the final one, in the finally block).
        var original = request;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (authenticate)
                {
                    var token = await _authenticator.GetTokenAsync(cancellationToken).ConfigureAwait(false);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                var response = await _httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!IsRetryable(response.StatusCode, idempotent) || attempt >= _maxRetries)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                _logger.LogDebug(
                    "Retrying request to {Uri} after {Delay} (attempt {Attempt}/{MaxRetries}, status {Status}).",
                    request.RequestUri, delay, attempt + 1, _maxRetries, (int)response.StatusCode);
                response.Dispose();

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);

                // HttpRequestMessage instances cannot be resent; clone for the retry.
                var contentBytes = request.Content is null
                    ? null
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var clone = CloneRequest(request, contentBytes);
                if (!ReferenceEquals(request, original))
                {
                    request.Dispose();
                }

                request = clone;
            }
        }
        finally
        {
            if (!ReferenceEquals(request, original))
            {
                request.Dispose();
            }
        }
    }

    /// <summary>
    /// 429 is always retryable (rate limiting rejects the request before it executes).
    /// 503 is only retried for idempotent requests: it can be returned after the server has
    /// already accepted the work, so resending a statement submission could execute DML twice.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode statusCode, bool idempotent)
        => statusCode == HttpStatusCode.TooManyRequests
            || (idempotent && statusCode == HttpStatusCode.ServiceUnavailable);

    /// <summary>
    /// Resolves the retry delay: honors <c>Retry-After</c> in both delta-seconds and
    /// HTTP-date forms (clamping past dates to zero), falling back to exponential backoff
    /// with jitter.
    /// </summary>
    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (retryAfter?.Date is { } date)
        {
            var untilDate = date - _timeProvider.GetUtcNow();
            return untilDate > TimeSpan.Zero ? untilDate : TimeSpan.Zero;
        }

        return _retryBaseDelay * Math.Pow(2, attempt) * (0.8 + Random.Shared.NextDouble() * 0.4);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, byte[]? contentBytes)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (contentBytes is not null && request.Content is not null)
        {
            var content = new ByteArrayContent(contentBytes);
            foreach (var (key, values) in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(key, values);
            }

            clone.Content = content;
        }

        foreach (var (key, values) in request.Headers)
        {
            if (!string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                clone.Headers.TryAddWithoutValidation(key, values);
            }
        }

        return clone;
    }

    private static DatabricksException CreateHttpException(HttpStatusCode statusCode, string body)
    {
        string? errorCode = null;
        var message = $"Databricks API request failed with status {(int)statusCode} ({statusCode}).";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error_code", out var codeElement))
            {
                errorCode = codeElement.GetString();
            }

            if (json.RootElement.TryGetProperty("message", out var messageElement)
                && messageElement.GetString() is { Length: > 0 } apiMessage)
            {
                message = $"{message} {apiMessage}";
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body; keep the generic message.
        }

        return new DatabricksException(message, (int)statusCode, errorCode, sqlState: null, statementId: null);
    }

    private static DatabricksException CreateStatementException(string statementId, StatementStatus status)
    {
        var error = status.Error;
        var message = status.State switch
        {
            "FAILED" => $"Statement '{statementId}' failed: {error?.Message ?? "no error details reported."}",
            "CANCELED" => $"Statement '{statementId}' was canceled.",
            _ => $"Statement '{statementId}' was closed before its result was fetched.",
        };

        return new DatabricksException(message, statusCode: 0, error?.ErrorCode, sqlState: null, statementId);
    }
}
