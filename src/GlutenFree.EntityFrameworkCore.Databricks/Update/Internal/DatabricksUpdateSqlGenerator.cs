using Microsoft.EntityFrameworkCore.Update;

namespace GlutenFree.EntityFrameworkCore.Databricks.Update.Internal;

/// <summary>Generates the DML EF issues from <c>SaveChanges</c>.</summary>
/// <remarks>
/// Databricks supports no <c>RETURNING</c>/<c>OUTPUT</c> clause for reading store-generated
/// values back, so the provider relies on client-generated keys (see
/// <c>planning/efcore-provider-plan.md</c> §2.2) and the relational base implementation is
/// sufficient for plain INSERT/UPDATE/DELETE.
/// </remarks>
public class DatabricksUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies)
{
}
