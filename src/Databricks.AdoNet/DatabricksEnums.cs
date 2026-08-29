namespace Databricks.AdoNet;

/// <summary>
/// Authentication mechanisms supported by the Databricks provider.
/// </summary>
public enum DatabricksAuthType
{
    /// <summary>Personal access token authentication (default).</summary>
    Pat = 0,

    /// <summary>OAuth machine-to-machine (service principal client credentials) authentication.</summary>
    OAuthM2M = 1,
}

/// <summary>
/// Result serialization format requested from the Databricks Statement Execution API.
/// </summary>
public enum DatabricksResultFormat
{
    /// <summary>Apache Arrow IPC stream (default; fastest and most type-faithful).</summary>
    Arrow = 0,

    /// <summary>JSON array of string values.</summary>
    Json = 1,
}

/// <summary>
/// Result disposition requested from the Databricks Statement Execution API.
/// </summary>
public enum DatabricksDisposition
{
    /// <summary>Let the server choose between inline results and external links.</summary>
    Auto = 0,

    /// <summary>Force inline results (subject to server-side size limits).</summary>
    Inline = 1,

    /// <summary>Force external (presigned URL) result links.</summary>
    ExternalLinks = 2,
}
