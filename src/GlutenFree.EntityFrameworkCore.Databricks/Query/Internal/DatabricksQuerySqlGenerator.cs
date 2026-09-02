using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>Generates Databricks SQL from a relational query expression tree.</summary>
public class DatabricksQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
    /// <inheritdoc />
    /// <remarks>
    /// Spark SQL's <c>+</c> is arithmetic only: applying it to strings coerces the operands to
    /// numbers and yields <c>NULL</c> rather than concatenating. Databricks spells string
    /// concatenation <c>||</c>.
    /// </remarks>
    protected override string GetOperator(SqlBinaryExpression binaryExpression)
        => binaryExpression.OperatorType == ExpressionType.Add && binaryExpression.Type == typeof(string)
            ? " || "
            : base.GetOperator(binaryExpression);

    /// <inheritdoc />
    /// <remarks>
    /// Databricks has no <c>CROSS APPLY</c>; the equivalent is a PostgreSQL-style
    /// <c>INNER JOIN LATERAL … ON TRUE</c>. EF's relational translator normally rewrites these
    /// shapes into <c>ROW_NUMBER()</c> subqueries, so this is defensive: without it a
    /// <see cref="CrossApplyExpression" /> would silently render SQL the server rejects.
    /// </remarks>
    protected override Expression VisitCrossApply(CrossApplyExpression crossApplyExpression)
    {
        Sql.Append("INNER JOIN LATERAL ");
        Visit(crossApplyExpression.Table);
        Sql.Append(" ON TRUE");

        return crossApplyExpression;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Databricks has no <c>OUTER APPLY</c>; the equivalent is
    /// <c>LEFT JOIN LATERAL … ON TRUE</c>. See <see cref="VisitCrossApply" />.
    /// </remarks>
    protected override Expression VisitOuterApply(OuterApplyExpression outerApplyExpression)
    {
        Sql.Append("LEFT JOIN LATERAL ");
        Visit(outerApplyExpression.Table);
        Sql.Append(" ON TRUE");

        return outerApplyExpression;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Databricks' <c>COUNT</c> returns <c>BIGINT</c>, but EF materializes
    /// <see cref="Queryable.Count{TSource}(IQueryable{TSource})" /> as an <see cref="int" /> and
    /// the data reader will not narrow a <c>BIGINT</c> column. Narrowing in SQL keeps the
    /// aggregate's semantics (<c>DISTINCT</c>, predicates and selectors are all still produced by
    /// the shared translator) while giving EF the type it expects. <c>LongCount</c> translates to
    /// a <see cref="long" />-typed <c>COUNT</c> and is left alone.
    /// </remarks>
    protected override Expression VisitSqlFunction(SqlFunctionExpression sqlFunctionExpression)
    {
        if (sqlFunctionExpression.Type == typeof(int)
            && string.Equals(sqlFunctionExpression.Name, "COUNT", StringComparison.OrdinalIgnoreCase))
        {
            Sql.Append("CAST(");
            base.VisitSqlFunction(sqlFunctionExpression);
            Sql.Append(" AS INT)");
            return sqlFunctionExpression;
        }

        return base.VisitSqlFunction(sqlFunctionExpression);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Databricks uses PostgreSQL-style <c>LIMIT</c>/<c>OFFSET</c> rather than
    /// <c>OFFSET ... FETCH</c>. <c>OFFSET</c> requires a <c>LIMIT</c>, and the limit must be an
    /// <c>INT</c> expression, so an unbounded skip is emitted as <c>LIMIT ALL</c>.
    /// </remarks>
    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        if (selectExpression.Limit is not null)
        {
            Sql.AppendLine().Append("LIMIT ");
            Visit(selectExpression.Limit);
        }
        else if (selectExpression.Offset is not null)
        {
            Sql.AppendLine().Append("LIMIT ALL");
        }

        if (selectExpression.Offset is not null)
        {
            Sql.AppendLine().Append("OFFSET ");
            Visit(selectExpression.Offset);
        }
    }
}
