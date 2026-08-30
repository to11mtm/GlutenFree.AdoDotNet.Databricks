using System.Runtime.CompilerServices;
using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.Databricks.AdoNet.Thrift;

namespace GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests;

/// <summary>
/// Opts every connection created by the shared <see cref="IntegrationConfig"/> into the
/// Thrift transport for this whole test assembly. The base integration project runs the
/// same suites over the default REST transport; this project re-runs them (via the
/// subclasses in <c>SharedSuiteReruns.cs</c>) over Thrift.
/// </summary>
internal static class ThriftTransportInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
        => IntegrationConfig.ConnectionCustomizer = connection => connection.UseThriftTransport();
}
