using System.Net;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>
/// Test double for HttpClient: replays scripted responses and records requests.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public FakeHttpHandler Enqueue(HttpStatusCode status, string jsonBody)
        => Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        });

    public FakeHttpHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return Respond(request, body);
    }

    protected override HttpResponseMessage Send(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            using var reader = new StreamReader(request.Content.ReadAsStream(cancellationToken));
            body = reader.ReadToEnd();
        }

        return Respond(request, body);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request, string? body)
    {
        Requests.Add((request, body));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeHttpHandler received an unexpected request: {request.Method} {request.RequestUri}");
        }

        return _responders.Dequeue()(request);
    }
}
