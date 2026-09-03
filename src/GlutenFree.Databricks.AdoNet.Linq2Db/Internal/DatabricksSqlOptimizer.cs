using LinqToDB;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

/// <summary>SQL optimizer for Databricks SQL.</summary>
public class DatabricksSqlOptimizer : BasicSqlOptimizer
{
    /// <summary>Creates the optimizer with the provider's flags.</summary>
    public DatabricksSqlOptimizer(SqlProviderFlags sqlProviderFlags)
        : base(sqlProviderFlags)
    {
    }

    /// <inheritdoc />
    public override SqlStatement TransformStatement(
        SqlStatement statement, DataOptions dataOptions, MappingSchema mappingSchema)
    {
        statement = base.TransformStatement(statement, dataOptions, mappingSchema);

        // Databricks DELETE/UPDATE do not support aliases/joins the way linq2db emits them
        // by default; rewrite to the WHERE ... IN (subquery) form like SQLite/PostgreSQL.
        return statement.QueryType switch
        {
            QueryType.Delete => GetAlternativeDelete((SqlDeleteStatement)statement),
            QueryType.Update => GetAlternativeUpdatePostgreSqlite(
                (SqlUpdateStatement)statement, dataOptions, mappingSchema),
            _ => statement,
        };
    }

    /// <inheritdoc />
    public override SqlExpressionConvertVisitor CreateConvertVisitor(bool allowModify)
        => new DatabricksSqlExpressionConvertVisitor(allowModify);

    private sealed class DatabricksSqlExpressionConvertVisitor(bool allowModify)
        : SqlExpressionConvertVisitor(allowModify)
    {
        /// <inheritdoc />
        /// <remarks>
        /// Databricks' <c>||</c> coerces a non-string operand to <c>STRING</c> on its own, so the
        /// explicit casts the base class adds for <c>+</c>-style concatenation are only noise.
        /// </remarks>
        protected override bool ConcatRequiresExplicitStringCast => false;
    }
}

