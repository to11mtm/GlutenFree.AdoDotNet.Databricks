using System.Collections.Concurrent;
using GlutenFree.Databricks.AdoNet.Auth;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// Process-wide shared HTTP resources. The REST transport is stateless, so all connections
/// share one <see cref="System.Net.Http.HttpClient"/> (one <see cref="SocketsHttpHandler"/>
/// connection pool) regardless of how many <see cref="DatabricksConnection"/> instances are
/// opened and closed — the ADO.NET "pooling" equivalent for this provider. OAuth
/// authenticators are shared per credential set so token caches survive across connections.
/// </summary>
internal static class DatabricksSharedResources
{
    /// <summary>
    /// The shared HTTP client. Never disposed; <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>
    /// keeps connection reuse DNS-rotation friendly.
    /// </summary>
    public static HttpClient HttpClient { get; } = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        },
        disposeHandler: true);

    private static readonly ConcurrentDictionary<(string Host, string ClientId, string ClientSecret), OAuthM2MAuthenticator>
        s_oauthAuthenticators = new();

    /// <summary>
    /// Returns the shared authenticator for a credential set, so cached tokens are reused
    /// across connection instances. Shared instances are never disposed.
    /// </summary>
    public static OAuthM2MAuthenticator GetOAuthAuthenticator(string host, string clientId, string clientSecret)
        => s_oauthAuthenticators.GetOrAdd(
            (host, clientId, clientSecret),
            static key => new OAuthM2MAuthenticator(key.Host, key.ClientId, key.ClientSecret, HttpClient));
}
