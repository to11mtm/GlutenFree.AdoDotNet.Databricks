using GlutenFree.EntityFrameworkCore.Databricks.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GlutenFree.EntityFrameworkCore.Databricks.Infrastructure;

/// <summary>
/// Allows Databricks-specific configuration to be performed on <see cref="DbContextOptions" />,
/// via the action passed to <c>UseDatabricks</c>.
/// </summary>
public class DatabricksDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<DatabricksDbContextOptionsBuilder, DatabricksOptionsExtension>(optionsBuilder)
{
    /// <summary>
    /// Sets the Unity Catalog catalog used to resolve unqualified names, overriding the
    /// connection string's <c>Catalog</c> keyword.
    /// </summary>
    public virtual DatabricksDbContextOptionsBuilder UseCatalog(string? catalog)
        => WithOption(e => e.WithCatalog(catalog));

    /// <summary>
    /// Sets the default schema used to resolve unqualified names, overriding the connection
    /// string's <c>Schema</c> keyword.
    /// </summary>
    public virtual DatabricksDbContextOptionsBuilder UseSchema(string? schema)
        => WithOption(e => e.WithSchema(schema));
}
