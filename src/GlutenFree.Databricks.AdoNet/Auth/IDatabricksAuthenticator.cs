namespace GlutenFree.Databricks.AdoNet.Auth;

/// <summary>
/// Supplies bearer tokens for authenticating requests to a Databricks workspace.
/// Implementations must be thread-safe.
/// </summary>
public interface IDatabricksAuthenticator
{
    /// <summary>
    /// Returns a valid bearer token, acquiring or refreshing it if necessary.
    /// </summary>
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous counterpart of <see cref="GetTokenAsync"/>. Implementations must use genuinely
    /// synchronous I/O: there is deliberately no default implementation, so an authenticator can
    /// never silently block the synchronous <c>Open()</c> path on async I/O.
    /// </summary>
    string GetToken(CancellationToken cancellationToken = default);
}
