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
        return connection;
    }

    private static IDatabricksTransport CreateTransport(
        DatabricksConnection connection, IReadOnlyDictionary<string, string>? driverOptions)
    {
        var settings = connection.Settings;

        var httpPath = settings.HttpPath.Length > 0
            ? settings.HttpPath
            : $"/sql/1.0/warehouses/{settings.EffectiveWarehouseId}";

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
}
