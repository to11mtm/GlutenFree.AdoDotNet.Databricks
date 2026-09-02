using Microsoft.EntityFrameworkCore.Query;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>Creates <see cref="DatabricksQuerySqlGenerator" /> instances.</summary>
public class DatabricksQuerySqlGeneratorFactory(QuerySqlGeneratorDependencies dependencies)
    : IQuerySqlGeneratorFactory
{
    /// <summary>Relational provider-specific dependencies for this service.</summary>
    protected virtual QuerySqlGeneratorDependencies Dependencies { get; } = dependencies;

    /// <inheritdoc />
    public virtual QuerySqlGenerator Create()
        => new DatabricksQuerySqlGenerator(Dependencies);
}
