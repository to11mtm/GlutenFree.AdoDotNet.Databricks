using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Thrift;

/// <summary>
/// Opts a <see cref="DatabricksConnection"/> into the Thrift (HiveServer2) transport.
/// </summary>
public static class DatabricksConnectionThriftExtensions
{
    /// <summary>
    /// Configures the connection to use the Thrift transport instead of the default
    /// REST Statement Execution API. Must be called before the connection is opened.
    /// Connection-string settings (host, credentials, warehouse, catalog/schema,
    /// timeouts) are reused as-is.
    /// </summary>
    /// <param name="connection">The unopened connection to configure.</param>
    /// <param name="driverOptions">
    /// Optional raw ADBC driver options (e.g. <c>adbc.databricks.cloudfetch.enabled</c>).
    /// </param>
    /// <returns>The same connection, for chaining.</returns>
    public static DatabricksConnection UseThriftTransport(
        this DatabricksConnection connection,
        IReadOnlyDictionary<string, string>? driverOptions = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.TransportFactory = conn => CreateTransport(conn, driverOptions);
        connection.DeclaredTransportSupportsTransactions = true;
        return connection;
    }

    private static IDatabricksTransport CreateTransport(
        DatabricksConnection connection, IReadOnlyDictionary<string, string>? driverOptions)
    {
        var settings = connection.Settings;
        var httpPath = ResolveHttpPath(settings.HttpPath, settings.EffectiveWarehouseId);

        var options = new ThriftTransportOptions
        {
            Token = settings.AuthType == DatabricksAuthType.Pat ? settings.Token : null,
            OAuthClientId = settings.AuthType == DatabricksAuthType.OAuthM2M ? settings.ClientId : null,
            OAuthClientSecret = settings.AuthType == DatabricksAuthType.OAuthM2M ? settings.ClientSecret : null,
            ConnectTimeout = TimeSpan.FromSeconds(settings.ConnectTimeout),
            DriverOptions = driverOptions ?? new Dictionary<string, string>(),
        };

        return new ThriftStatementTransport(settings.Host, httpPath, options);
    }

    /// <summary>
    /// An explicit HttpPath is used directly — this is how all-purpose cluster endpoints
    /// (<c>/sql/protocolv1/o/&lt;org-id&gt;/&lt;cluster-id&gt;</c>) are reached, since only the
    /// Thrift protocol supports them. It is normalized to a leading '/' (cluster-path
    /// detection tolerates its absence, but the driver needs a valid absolute URL path).
    /// Otherwise the SQL warehouse path is derived from the warehouse id.
    /// </summary>
    internal static string ResolveHttpPath(string httpPath, string effectiveWarehouseId)
        => httpPath.Length > 0
            ? (httpPath.StartsWith('/') ? httpPath : "/" + httpPath)
            : $"/sql/1.0/warehouses/{effectiveWarehouseId}";
}
