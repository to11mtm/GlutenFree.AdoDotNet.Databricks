using System.Data.Common;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// Strongly-typed connection string builder for Databricks connections.
/// </summary>
/// <remarks>
/// Recognized keywords (case-insensitive):
/// <c>Host</c>, <c>HttpPath</c>, <c>WarehouseId</c>, <c>AuthType</c>, <c>Token</c>,
/// <c>ClientId</c>, <c>ClientSecret</c>, <c>Catalog</c>, <c>Schema</c>,
/// <c>CommandTimeout</c>, <c>ConnectTimeout</c>, <c>ResultFormat</c>, <c>Disposition</c>,
/// <c>MaxRetries</c>, <c>RetryBaseDelay</c>, <c>Pooling</c>.
/// </remarks>
public sealed class DatabricksConnectionStringBuilder : DbConnectionStringBuilder
{
    private static readonly Dictionary<string, string> s_canonicalKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Host"] = "Host",
        ["HttpPath"] = "HttpPath",
        ["Http Path"] = "HttpPath",
        ["WarehouseId"] = "WarehouseId",
        ["Warehouse Id"] = "WarehouseId",
        ["AuthType"] = "AuthType",
        ["Auth Type"] = "AuthType",
        ["Token"] = "Token",
        ["ClientId"] = "ClientId",
        ["Client Id"] = "ClientId",
        ["ClientSecret"] = "ClientSecret",
        ["Client Secret"] = "ClientSecret",
        ["Catalog"] = "Catalog",
        ["Schema"] = "Schema",
        ["CommandTimeout"] = "CommandTimeout",
        ["Command Timeout"] = "CommandTimeout",
        ["ConnectTimeout"] = "ConnectTimeout",
        ["Connect Timeout"] = "ConnectTimeout",
        ["ResultFormat"] = "ResultFormat",
        ["Result Format"] = "ResultFormat",
        ["Disposition"] = "Disposition",
        ["MaxRetries"] = "MaxRetries",
        ["Max Retries"] = "MaxRetries",
        ["RetryBaseDelay"] = "RetryBaseDelay",
        ["Retry Base Delay"] = "RetryBaseDelay",
        // Accepted for ADO.NET connection-string compatibility; the REST transport is
        // stateless HTTP, so there is no physical connection pool to enable or disable.
        ["Pooling"] = "Pooling",
    };

    private static readonly HashSet<string> s_secretKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Token",
        "ClientSecret",
    };

    /// <summary>Initializes an empty builder.</summary>
    public DatabricksConnectionStringBuilder()
    {
    }

    /// <summary>Initializes a builder from an existing connection string.</summary>
    public DatabricksConnectionStringBuilder(string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            ConnectionString = connectionString;
        }
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override object this[string keyword]
    {
        get => base[Canonicalize(keyword)];
        set => base[Canonicalize(keyword)] = value;
    }

    /// <summary>Databricks workspace URL, e.g. <c>https://adb-1234567890.11.azuredatabricks.net</c>.</summary>
    public string Host
    {
        get => GetString("Host");
        set => this["Host"] = value;
    }

    /// <summary>
    /// SQL warehouse HTTP path, e.g. <c>/sql/1.0/warehouses/abcdef1234567890</c>.
    /// Alternative to <see cref="WarehouseId"/>.
    /// </summary>
    public string HttpPath
    {
        get => GetString("HttpPath");
        set => this["HttpPath"] = value;
    }

    /// <summary>SQL warehouse identifier. Alternative to <see cref="HttpPath"/>.</summary>
    public string WarehouseId
    {
        get => GetString("WarehouseId");
        set => this["WarehouseId"] = value;
    }

    /// <summary>Authentication mechanism. Defaults to <see cref="DatabricksAuthType.Pat"/>.</summary>
    public DatabricksAuthType AuthType
    {
        get => GetEnum("AuthType", DatabricksAuthType.Pat);
        set => this["AuthType"] = value.ToString();
    }

    /// <summary>Personal access token (used when <see cref="AuthType"/> is <see cref="DatabricksAuthType.Pat"/>).</summary>
    public string Token
    {
        get => GetString("Token");
        set => this["Token"] = value;
    }

    /// <summary>OAuth service principal client id (used when <see cref="AuthType"/> is <see cref="DatabricksAuthType.OAuthM2M"/>).</summary>
    public string ClientId
    {
        get => GetString("ClientId");
        set => this["ClientId"] = value;
    }

    /// <summary>OAuth service principal client secret (used when <see cref="AuthType"/> is <see cref="DatabricksAuthType.OAuthM2M"/>).</summary>
    public string ClientSecret
    {
        get => GetString("ClientSecret");
        set => this["ClientSecret"] = value;
    }

    /// <summary>Initial catalog for the session.</summary>
    public string Catalog
    {
        get => GetString("Catalog");
        set => this["Catalog"] = value;
    }

    /// <summary>Initial schema for the session.</summary>
    public string Schema
    {
        get => GetString("Schema");
        set => this["Schema"] = value;
    }

    /// <summary>Default statement timeout in seconds. 0 (default) uses the server default.</summary>
    public int CommandTimeout
    {
        get => GetInt32("CommandTimeout", 0);
        set => this["CommandTimeout"] = SetNonNegative(value);
    }

    /// <summary>Timeout in seconds for establishing/validating a connection. Default 30.</summary>
    public int ConnectTimeout
    {
        get => GetInt32("ConnectTimeout", 30);
        set => this["ConnectTimeout"] = SetNonNegative(value);
    }

    /// <summary>Result serialization format. Defaults to <see cref="DatabricksResultFormat.Arrow"/>.</summary>
    public DatabricksResultFormat ResultFormat
    {
        get => GetEnum("ResultFormat", DatabricksResultFormat.Arrow);
        set => this["ResultFormat"] = value.ToString();
    }

    /// <summary>Result disposition. Defaults to <see cref="DatabricksDisposition.Auto"/>.</summary>
    public DatabricksDisposition Disposition
    {
        get => GetEnum("Disposition", DatabricksDisposition.Auto);
        set => this["Disposition"] = value.ToString();
    }

    /// <summary>Maximum retries for transient HTTP failures (429/503). Default 4.</summary>
    public int MaxRetries
    {
        get => GetInt32("MaxRetries", 4);
        set => this["MaxRetries"] = SetNonNegative(value);
    }

    /// <summary>Base delay in milliseconds for exponential retry backoff. Default 500.</summary>
    public int RetryBaseDelay
    {
        get => GetInt32("RetryBaseDelay", 500);
        set => this["RetryBaseDelay"] = SetNonNegative(value);
    }

    /// <summary>
    /// Accepted for compatibility; has no effect. The REST transport is stateless HTTP,
    /// so HTTP handlers and OAuth tokens are always shared where safe.
    /// </summary>
    public bool Pooling
    {
        get => !TryGetValue("Pooling", out var value) || Convert.ToBoolean(value);
        set => this["Pooling"] = value;
    }

    /// <summary>
    /// True when <see cref="HttpPath"/> points at an all-purpose (interactive) cluster
    /// (<c>/sql/protocolv1/o/&lt;org-id&gt;/&lt;cluster-id&gt;</c>) rather than a SQL warehouse.
    /// Cluster endpoints only speak the Thrift protocol, so they require the
    /// <c>GlutenFree.Databricks.AdoNet.Thrift</c> transport add-on.
    /// </summary>
    public bool IsAllPurposeClusterPath
        => HttpPath.TrimStart('/').StartsWith("sql/protocolv1/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The effective warehouse id, resolved from <see cref="WarehouseId"/> or parsed
    /// from the trailing segment of a warehouse-shaped <see cref="HttpPath"/>. Empty for
    /// all-purpose cluster paths — a cluster id is not a warehouse id.
    /// </summary>
    public string EffectiveWarehouseId
    {
        get
        {
            var explicitId = WarehouseId;
            if (explicitId.Length > 0)
            {
                return explicitId;
            }

            var httpPath = HttpPath;
            if (httpPath.Length > 0 && !IsAllPurposeClusterPath)
            {
                var lastSlash = httpPath.TrimEnd('/').LastIndexOf('/');
                return lastSlash >= 0 ? httpPath.TrimEnd('/')[(lastSlash + 1)..] : httpPath;
            }

            return string.Empty;
        }
    }

    /// <inheritdoc />
    public override bool ContainsKey(string keyword) => base.ContainsKey(Canonicalize(keyword));

    /// <inheritdoc />
    public override bool Remove(string keyword) => base.Remove(Canonicalize(keyword));

    /// <inheritdoc />
    public override bool TryGetValue(string keyword, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? value)
        => base.TryGetValue(Canonicalize(keyword), out value);

    /// <summary>
    /// Returns the connection string with secret values (<c>Token</c>, <c>ClientSecret</c>) redacted.
    /// Suitable for logging and diagnostics.
    /// </summary>
    public string ToDisplayString()
    {
        var copy = new DatabricksConnectionStringBuilder(ConnectionString);
        foreach (var secret in s_secretKeywords)
        {
            if (copy.ContainsKey(secret))
            {
                copy[secret] = "*****";
            }
        }

        return copy.ConnectionString;
    }

    /// <summary>
    /// Validates that the builder describes a usable connection, throwing
    /// <see cref="ArgumentException"/> with a descriptive message otherwise.
    /// </summary>
    public void Validate()
    {
        if (Host.Length == 0)
        {
            throw new ArgumentException("Connection string must specify 'Host' (the Databricks workspace URL).");
        }

        if (!Uri.TryCreate(Host, UriKind.Absolute, out var hostUri)
            || hostUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"'Host' must be an absolute https URL; got '{Host}'. " +
                "Bearer credentials must never be sent over plaintext http.");
        }

        if (EffectiveWarehouseId.Length == 0 && !IsAllPurposeClusterPath)
        {
            throw new ArgumentException(
                "Connection string must specify 'WarehouseId' or 'HttpPath' identifying a SQL warehouse "
                + "(or an all-purpose cluster HttpPath, which requires the Thrift transport add-on).");
        }

        switch (AuthType)
        {
            case DatabricksAuthType.Pat when Token.Length == 0:
                throw new ArgumentException("'Token' is required when AuthType=Pat.");
            case DatabricksAuthType.OAuthM2M when ClientId.Length == 0 || ClientSecret.Length == 0:
                throw new ArgumentException("'ClientId' and 'ClientSecret' are required when AuthType=OAuthM2M.");
        }
    }

    private static string Canonicalize(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (!s_canonicalKeywords.TryGetValue(keyword.Trim(), out var canonical))
        {
            throw new ArgumentException($"Keyword '{keyword}' is not supported by the Databricks connection string.", nameof(keyword));
        }

        return canonical;
    }

    private static int SetNonNegative(int value)
        => value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Value must be non-negative.");

    private string GetString(string keyword)
        => TryGetValue(keyword, out var value) ? Convert.ToString(value) ?? string.Empty : string.Empty;

    private int GetInt32(string keyword, int defaultValue)
        => TryGetValue(keyword, out var value) ? Convert.ToInt32(value) : defaultValue;

    private TEnum GetEnum<TEnum>(string keyword, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!TryGetValue(keyword, out var value) || value is null)
        {
            return defaultValue;
        }

        var text = Convert.ToString(value);
        if (string.IsNullOrEmpty(text))
        {
            return defaultValue;
        }

        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"'{text}' is not a valid value for '{keyword}'. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
