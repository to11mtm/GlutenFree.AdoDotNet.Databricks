using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlutenFree.EntityFrameworkCore.Databricks.Migrations.Internal;

/// <summary>
/// Stands in for EF's migrator so that calling <c>Database.Migrate()</c> fails with an
/// explanation instead of a dependency-injection error about a missing
/// <see cref="IHistoryRepository" />.
/// </summary>
/// <remarks>
/// Lakehouse schema is normally owned by Databricks — Unity Catalog, Delta Live Tables or a
/// deployment pipeline — rather than by an application ORM, and Delta's DDL surface does not line
/// up with EF's migration model. See <c>planning/efcore-provider-plan.md</c> §6;
/// <c>EnsureCreated</c>/<c>EnsureDeleted</c> still work and manage the schema itself.
/// </remarks>
public class DatabricksMigrator : IMigrator
{
    private const string NotSupportedMessage =
        "The Databricks provider does not support EF Core Migrations. Manage table schema in "
        + "Databricks instead (Unity Catalog DDL, Delta Live Tables or your deployment pipeline) "
        + "and map the existing tables with 'ToTable(...)'. 'EnsureCreated'/'EnsureDeleted' are "
        + "supported and create or drop the mapped Unity Catalog schema.";

    /// <inheritdoc />
    [RequiresUnreferencedCode("Migration generation currently isn't compatible with trimming")]
    [RequiresDynamicCode("Migrations operations are not supported with NativeAOT")]
    public virtual void Migrate(string? targetMigration = null)
        => throw new NotSupportedException(NotSupportedMessage);

    /// <inheritdoc />
    [RequiresUnreferencedCode("Migration generation currently isn't compatible with trimming")]
    [RequiresDynamicCode("Migrations operations are not supported with NativeAOT")]
    public virtual Task MigrateAsync(string? targetMigration = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotSupportedMessage);

    /// <inheritdoc />
    [RequiresUnreferencedCode("Migration generation currently isn't compatible with trimming")]
    [RequiresDynamicCode("Migrations operations are not supported with NativeAOT")]
    public virtual string GenerateScript(
        string? fromMigration = null,
        string? toMigration = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
        => throw new NotSupportedException(NotSupportedMessage);

    /// <inheritdoc />
    [RequiresDynamicCode("Migrations operations are not supported with NativeAOT")]
    public virtual bool HasPendingModelChanges()
        => throw new NotSupportedException(NotSupportedMessage);
}
