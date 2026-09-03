using Microsoft.EntityFrameworkCore.Update;

namespace GlutenFree.EntityFrameworkCore.Databricks.Update.Internal;

/// <summary>
/// A batch that submits every statement in one <c>BEGIN ATOMIC ... END;</c> compound statement,
/// so a <c>SaveChanges</c> is all-or-nothing without an explicit transaction.
/// </summary>
/// <remarks>
/// <para>
/// Databricks' non-interactive transactions work on any SQL warehouse and, unlike
/// <c>BEGIN TRANSACTION</c>, do not need a stateful session — which makes them the only way to
/// get atomicity over the REST Statement Execution API. The whole block is one submission, so
/// this also collapses N round trips into one.
/// </para>
/// <para>
/// The trade-off is that the block reports no per-statement rows affected. EF normally uses that
/// number to detect concurrency violations, so this batch does not verify it;
/// <see cref="GlutenFree.EntityFrameworkCore.Databricks.Internal.DatabricksModelValidator" />
/// refuses concurrency tokens for the same
/// reason, and Delta's own optimistic concurrency still fails the whole block on a conflicting
/// write. Because keys are client-generated there is nothing to read back either, so every
/// command's <see cref="ResultSetMapping" /> is <see cref="ResultSetMapping.NoResults" /> and the
/// inherited consume logic is a no-op.
/// </para>
/// <para>
/// A single-command batch is left unwrapped: one statement is already atomic, and wrapping it
/// would only obscure the error message when it fails.
/// </para>
/// </remarks>
public class DatabricksAtomicModificationCommandBatch(
    ModificationCommandBatchFactoryDependencies dependencies,
    int? maxBatchSize = null)
    : AffectedCountModificationCommandBatch(dependencies, maxBatchSize)
{
    /// <inheritdoc />
    public override void Complete(bool moreBatchesExpected)
    {
        if (ModificationCommands.Count > 1)
        {
            SqlBuilder.Insert(0, "BEGIN ATOMIC" + Environment.NewLine);
            SqlBuilder.Append("END;").AppendLine();
        }

        // The compound statement provides the atomicity, so EF must not try to open a
        // transaction of its own — the REST transport cannot begin one at all.
        SetRequiresTransaction(false);

        base.Complete(moreBatchesExpected);
    }
}
