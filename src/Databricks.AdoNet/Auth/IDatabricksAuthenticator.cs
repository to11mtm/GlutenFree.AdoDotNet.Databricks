namespace Databricks.AdoNet.Auth;

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
}
