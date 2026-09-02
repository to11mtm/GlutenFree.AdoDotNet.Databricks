using Microsoft.EntityFrameworkCore.Update;

namespace GlutenFree.EntityFrameworkCore.Databricks.Update.Internal;

/// <summary>Creates the modification command batches EF uses for <c>SaveChanges</c>.</summary>
/// <remarks>
/// One statement per batch: Databricks executes a single statement per request on the REST
/// transport, so multi-statement batches would buy nothing, and a batch that Databricks accepts
/// as one submission (<c>BEGIN ATOMIC ... END;</c>) cannot report per-statement rows affected.
/// Grouping several commands atomically is planned as a separate batch implementation; see
/// <c>planning/efcore-provider-plan.md</c> §2.1.
/// </remarks>
public class DatabricksModificationCommandBatchFactory(ModificationCommandBatchFactoryDependencies dependencies)
    : IModificationCommandBatchFactory
{
    /// <summary>Relational provider-specific dependencies for this service.</summary>
    protected virtual ModificationCommandBatchFactoryDependencies Dependencies { get; } = dependencies;

    /// <inheritdoc />
    public virtual ModificationCommandBatch Create()
        => new SingularModificationCommandBatch(Dependencies);
}
