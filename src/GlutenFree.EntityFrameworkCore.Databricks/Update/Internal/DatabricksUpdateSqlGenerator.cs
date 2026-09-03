using System.Text;
using Microsoft.EntityFrameworkCore.Update;

namespace GlutenFree.EntityFrameworkCore.Databricks.Update.Internal;

/// <summary>Generates the DML EF issues from <c>SaveChanges</c>.</summary>
/// <remarks>
/// <para>
/// Databricks supports no <c>RETURNING</c>/<c>OUTPUT</c> clause, so the provider relies on
/// client-generated keys (see <c>planning/efcore-provider-plan.md</c> §2.2) and never asks the
/// store for a value back.
/// </para>
/// <para>
/// The relational base still appends <c>RETURNING 1</c> to every <c>UPDATE</c> and
/// <c>DELETE</c> — it uses that constant to learn how many rows were affected, for optimistic
/// concurrency. Databricks rejects it as a syntax error, so those two operations are overridden
/// to emit plain DML and report <see cref="ResultSetMapping.NoResults" />. Concurrency tokens are
/// refused by
/// <see cref="GlutenFree.EntityFrameworkCore.Databricks.Internal.DatabricksModelValidator" />
/// precisely because that signal is unavailable.
/// </para>
/// </remarks>
public class DatabricksUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies)
{
    /// <inheritdoc />
    public override ResultSetMapping AppendUpdateOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        AppendUpdateCommand(
            commandStringBuilder,
            command.TableName,
            command.Schema,
            [.. command.ColumnModifications.Where(o => o.IsWrite)],
            readOperations: [],
            [.. command.ColumnModifications.Where(o => o.IsCondition)]);

        requiresTransaction = false;

        return ResultSetMapping.NoResults;
    }

    /// <inheritdoc />
    public override ResultSetMapping AppendDeleteOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        AppendDeleteCommand(
            commandStringBuilder,
            command.TableName,
            command.Schema,
            readOperations: [],
            [.. command.ColumnModifications.Where(o => o.IsCondition)]);

        requiresTransaction = false;

        return ResultSetMapping.NoResults;
    }
}
