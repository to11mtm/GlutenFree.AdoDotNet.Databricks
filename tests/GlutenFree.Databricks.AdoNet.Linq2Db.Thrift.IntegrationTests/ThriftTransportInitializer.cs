using System.Runtime.CompilerServices;
using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.Databricks.AdoNet.Thrift;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Thrift.IntegrationTests;

/// <summary>
/// Opts every connection created by the shared <see cref="IntegrationConfig"/> into the
/// Thrift transport for this whole test assembly, so schema-setup connections (opened
/// before linq2db gets involved) use the same session-based transport as the
/// <c>DatabricksThriftTools</c>-created data connections.
/// </summary>
internal static class ThriftTransportInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
        => IntegrationConfig.ConnectionCustomizer = connection => connection.UseThriftTransport();
}
