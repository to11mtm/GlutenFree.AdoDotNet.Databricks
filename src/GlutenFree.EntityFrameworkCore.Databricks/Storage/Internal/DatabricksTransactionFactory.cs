using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// Creates <see cref="RelationalTransaction" /> wrappers that report no savepoint support.
/// </summary>
/// <remarks>
/// Databricks has no <c>SAVEPOINT</c>. Reporting that here makes EF skip the savepoint path it
/// would otherwise take between batches in <c>SaveChanges</c>.
/// </remarks>
public class DatabricksTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
    : IRelationalTransactionFactory
{
    /// <summary>Relational provider-specific dependencies for this service.</summary>
    protected virtual RelationalTransactionFactoryDependencies Dependencies { get; } = dependencies;

    /// <inheritdoc />
    public virtual RelationalTransaction Create(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned)
        => new DatabricksTransaction(connection, transaction, transactionId, logger, transactionOwned, Dependencies.SqlGenerationHelper);
}

/// <summary>A <see cref="RelationalTransaction" /> that does not support savepoints.</summary>
public class DatabricksTransaction(
    IRelationalConnection connection,
    DbTransaction transaction,
    Guid transactionId,
    IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
    bool transactionOwned,
    ISqlGenerationHelper sqlGenerationHelper)
    : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
{
    /// <inheritdoc />
    /// <remarks>Databricks has no <c>SAVEPOINT</c> statement.</remarks>
    public override bool SupportsSavepoints
        => false;
}
