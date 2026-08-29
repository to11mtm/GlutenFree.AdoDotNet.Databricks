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
    /// Synchronous counterpart of <see cref="GetTokenAsync"/>. Implementations should use
    /// genuinely synchronous I/O where possible; the default blocks on the async path.
    /// </summary>
    string GetToken(CancellationToken cancellationToken = default)
        => GetTokenAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
}
