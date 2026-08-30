namespace GlutenFree.Databricks.AdoNet.Auth;

/// <summary>
/// Authenticates using a Databricks personal access token.
/// </summary>
public sealed class PatAuthenticator : IDatabricksAuthenticator
{
    private readonly string _token;

    /// <summary>Creates an authenticator that always presents <paramref name="token"/>.</summary>
    public PatAuthenticator(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        _token = token;
    }

    /// <inheritdoc />
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_token);

    /// <inheritdoc />
    public string GetToken(CancellationToken cancellationToken = default) => _token;
}
