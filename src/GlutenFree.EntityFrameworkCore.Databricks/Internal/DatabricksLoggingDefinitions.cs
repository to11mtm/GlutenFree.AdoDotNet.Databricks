using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GlutenFree.EntityFrameworkCore.Databricks.Internal;

/// <summary>
/// Logging definitions for the Databricks provider. Every EF Core provider must register a
/// <see cref="LoggingDefinitions"/> implementation; the relational base supplies the shared
/// event definitions, and provider-specific ones are added here as they are introduced.
/// </summary>
public class DatabricksLoggingDefinitions : RelationalLoggingDefinitions
{
}
