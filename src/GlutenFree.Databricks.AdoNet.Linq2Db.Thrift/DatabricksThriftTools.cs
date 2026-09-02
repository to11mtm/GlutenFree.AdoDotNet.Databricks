using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.Linq2Db.Internal;
using GlutenFree.Databricks.AdoNet.Thrift;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Thrift;

/// <summary>
/// Entry points for using linq2db with Databricks over the Thrift (session-based) transport,
/// which — unlike the default REST transport — supports interactive transactions
/// (<c>BEGIN TRANSACTION</c> … <c>COMMIT</c>/<c>ROLLBACK</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every connection this provider creates is opted into the Thrift transport via
/// <see cref="DatabricksConnectionThriftExtensions.UseThriftTransport"/>, and the provider
/// declares <c>TransactionsSupported=true</c>, so linq2db's
/// <see cref="DataConnection.BeginTransaction()"/> drives a real
/// <see cref="DatabricksTransaction"/> on the connection's server-side session.
/// </para>
/// <para>
/// Databricks transaction requirements apply (see <see cref="DatabricksTransaction"/>):
/// every table written to must be a Unity Catalog managed Delta/Iceberg table with catalog
/// commits enabled, DDL/metadata operations are not allowed inside a transaction, only one
/// transaction may be active per connection, there are no savepoints, and conflicts are
/// detected optimistically at commit time — be prepared to retry.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var db = DatabricksThriftTools.CreateDataConnection(
///     "Host=https://adb-123.azuredatabricks.net;WarehouseId=abc;Token=dapi...");
/// using var tx = db.BeginTransaction();
/// db.Insert(new Order { Id = 1, Amount = 9.99m });
/// tx.Commit();
/// </code>
/// </example>
public static class DatabricksThriftTools
{
    private static readonly DatabricksDataProvider s_provider = new(
        DatabricksProviderName.DatabricksThrift,
        transactionsSupported: true,
        configureConnection: connection => connection.UseThriftTransport());

    /// <summary>The singleton Thrift-flavored Databricks data provider instance.</summary>
    public static IDataProvider GetDataProvider() => s_provider;

    /// <summary>
    /// Creates a <see cref="DataConnection"/> from a GlutenFree.Databricks.AdoNet connection
    /// string; the underlying connection uses the Thrift transport.
    /// </summary>
    public static DataConnection CreateDataConnection(string connectionString)
        => new(new DataOptions().UseConnectionString(s_provider, connectionString));

    /// <summary>
    /// Creates a <see cref="DataConnection"/> over an existing (open or closed) connection.
    /// A connection with no transport configured is opted into the Thrift transport; a
    /// connection whose transport is already configured is used as-is.
    /// </summary>
    public static DataConnection CreateDataConnection(DatabricksConnection connection)
        => new(new DataOptions().UseConnection(s_provider, EnsureThriftTransport(connection)));

    /// <summary>
    /// Adds the Thrift-flavored Databricks provider to a <see cref="DataOptions"/> pipeline;
    /// connections created from the connection string use the Thrift transport.
    /// </summary>
    public static DataOptions UseDatabricksThrift(this DataOptions options, string connectionString)
        => options.UseConnectionString(s_provider, connectionString);

    /// <summary>
    /// Adds the Thrift-flavored Databricks provider with an existing connection to a
    /// <see cref="DataOptions"/> pipeline. A connection with no transport configured is
    /// opted into the Thrift transport; a connection whose transport is already configured
    /// is used as-is.
    /// </summary>
    public static DataOptions UseDatabricksThrift(this DataOptions options, DatabricksConnection connection)
        => options.UseConnection(s_provider, EnsureThriftTransport(connection));

    /// <summary>
    /// Defensively opts an unconfigured connection into the Thrift transport, so the
    /// provider flavor and the transport cannot disagree. A connection with a transport
    /// factory already set (Thrift, or a test transport) is left untouched.
    /// </summary>
    private static DatabricksConnection EnsureThriftTransport(DatabricksConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.TransportFactory is null ? connection.UseThriftTransport() : connection;
    }
}
