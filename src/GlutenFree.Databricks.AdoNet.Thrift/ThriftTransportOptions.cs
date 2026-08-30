namespace GlutenFree.Databricks.AdoNet.Thrift;

/// <summary>
/// Options for <see cref="ThriftStatementTransport"/>. Exactly one credential form is
/// required: a personal access token, or an OAuth M2M client id/secret pair.
/// </summary>
public sealed class ThriftTransportOptions
{
    /// <summary>Personal access token.</summary>
    public string? Token { get; init; }

    /// <summary>OAuth service principal client id (M2M).</summary>
    public string? OAuthClientId { get; init; }

    /// <summary>OAuth service principal client secret (M2M).</summary>
    public string? OAuthClientSecret { get; init; }

    /// <summary>Timeout for establishing the Thrift session. Zero uses the driver default.</summary>
    public TimeSpan ConnectTimeout { get; init; }

    /// <summary>
    /// Additional raw ADBC driver options (e.g. <c>adbc.databricks.cloudfetch.enabled</c>)
    /// applied after — and overriding — the options derived from this instance.
    /// </summary>
    public IReadOnlyDictionary<string, string> DriverOptions { get; init; }
        = new Dictionary<string, string>();
}
