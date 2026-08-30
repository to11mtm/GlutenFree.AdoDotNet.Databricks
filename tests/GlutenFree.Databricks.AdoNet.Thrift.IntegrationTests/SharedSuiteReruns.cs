using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.Databricks.AdoNet.Linq2Db.IntegrationTests;

namespace GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests;

// Re-runs the shared integration suites over the Thrift transport (the module initializer
// routes every IntegrationConfig connection through UseThriftTransport). xunit discovers
// and runs the inherited [IntegrationFact] methods; no test code is duplicated.

/// <inheritdoc />
public sealed class ThriftDatabricksIntegrationTests : DatabricksIntegrationTests;

/// <inheritdoc />
public sealed class ThriftNumericTypesIntegrationTests : NumericTypesIntegrationTests;

/// <inheritdoc />
public sealed class ThriftExtendedTypesIntegrationTests : ExtendedTypesIntegrationTests;

// linq2db data-provider suites — SQL generation is transport-independent, but these
// prove the full linq2db → ADO.NET → Thrift pipeline end-to-end.

/// <inheritdoc />
public sealed class ThriftLinq2DbIntegrationTests : Linq2DbIntegrationTests;

/// <inheritdoc />
public sealed class ThriftLinq2DbDialectIntegrationTests : Linq2DbDialectIntegrationTests;

/// <inheritdoc />
public sealed class ThriftLinq2DbExtendedTypesIntegrationTests : Linq2DbExtendedTypesIntegrationTests;

/// <inheritdoc />
public sealed class ThriftLinq2DbSqlBitsIntegrationTests : Linq2DbSqlBitsIntegrationTests;
