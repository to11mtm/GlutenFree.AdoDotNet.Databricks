using GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

namespace GlutenFree.EntityFrameworkCore.Databricks.Thrift.IntegrationTests;

// Re-runs the EF Core integration suites over the Thrift transport (the module initializer
// routes every IntegrationConfig connection through UseThriftTransport). xunit discovers and
// runs the inherited [IntegrationFact] methods; no test code is duplicated.
//
// SQL generation is transport-independent, so what this really proves is the full
// EF Core → ADO.NET → Thrift pipeline: parameter binding, result decoding and, unlike REST,
// a connection that can begin a real transaction.

/// <inheritdoc />
public sealed class ThriftEfCoreQueryIntegrationTests : EfCoreQueryIntegrationTests;

/// <inheritdoc />
public sealed class ThriftEfCoreTranslationIntegrationTests : EfCoreTranslationIntegrationTests;

/// <inheritdoc />
public sealed class ThriftEfCoreBulkOperationIntegrationTests : EfCoreBulkOperationIntegrationTests;
