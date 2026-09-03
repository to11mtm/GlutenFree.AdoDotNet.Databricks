using GlutenFree.Databricks.AdoNet;
using GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace GlutenFree.EntityFrameworkCore.Databricks.Update.Internal;

/// <summary>Creates the modification command batches EF uses for <c>SaveChanges</c>.</summary>
/// <remarks>
/// Which batch to use depends on what the connection can do and on whether the caller already has
/// a transaction open:
/// <list type="bullet">
/// <item><description>Over the stateless REST transport, which cannot begin a transaction at all,
/// <see cref="DatabricksAtomicModificationCommandBatch" /> submits every statement inside one
/// <c>BEGIN ATOMIC ... END;</c> block.</description></item>
/// <item><description>Over Thrift, statements are sent one at a time and EF opens a real
/// transaction around them. That is both closer to what EF expects and the only option available:
/// Thrift binds parameters through <c>EXECUTE IMMEDIATE</c>, which rejects compound
/// statements.</description></item>
/// <item><description>Inside a caller-started transaction, statements are likewise sent one at a
/// time — the transaction already provides atomicity.</description></item>
/// <item><description>When the caller sets <see cref="AutoTransactionBehavior.Never" />,
/// statements are sent one at a time with no transaction at all. That is the escape hatch for
/// tables without Delta's <c>catalogManaged</c> feature, which cannot be written to
/// transactionally.</description></item>
/// </list>
/// See <c>planning/efcore-provider-plan.md</c> §2.1.
/// </remarks>
public class DatabricksModificationCommandBatchFactory(
    ModificationCommandBatchFactoryDependencies dependencies,
    IDatabricksRelationalConnection connection)
    : IModificationCommandBatchFactory
{
    /// <summary>Relational provider-specific dependencies for this service.</summary>
    protected virtual ModificationCommandBatchFactoryDependencies Dependencies { get; } = dependencies;

    /// <inheritdoc />
    public virtual ModificationCommandBatch Create()
        => UseAtomicBatch()
            ? new DatabricksAtomicModificationCommandBatch(Dependencies)
            : new SingularModificationCommandBatch(Dependencies);

    /// <summary>
    /// Whether this save should be wrapped in a compound statement.
    /// </summary>
    /// <remarks>
    /// Only when nothing better is available. A compound statement is submitted through
    /// <c>EXECUTE IMMEDIATE</c> on the Thrift transport, which rejects SQL scripts, so it is
    /// strictly the fallback for transports that cannot begin a real transaction — the stateless
    /// REST one. It is also skipped when the caller already has a transaction open, and when they
    /// set <see cref="AutoTransactionBehavior.Never" />, which both says this save need not be
    /// atomic and provides the escape hatch for tables without Delta's <c>catalogManaged</c>
    /// feature (a compound statement cannot write to those at all).
    /// </remarks>
    private bool UseAtomicBatch()
        => connection.CurrentTransaction is null
            && Dependencies.CurrentContext.Context.Database.AutoTransactionBehavior
            != AutoTransactionBehavior.Never
            && connection.DbConnection is DatabricksConnection { SupportsTransactions: false };
}
