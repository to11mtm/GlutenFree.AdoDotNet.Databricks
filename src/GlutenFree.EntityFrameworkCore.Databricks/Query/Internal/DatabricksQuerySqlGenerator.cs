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
    /// Databricks widens the result type of some aggregates beyond what EF materializes them as,
    /// and the data reader will not narrow a value implicitly:
    /// <list type="bullet">
    /// <item><description><c>COUNT</c> returns <c>BIGINT</c>, but
    /// <see cref="Queryable.Count{TSource}(IQueryable{TSource})" /> materializes as an
    /// <see cref="int" /> (<c>LongCount</c> produces a <see cref="long" />-typed <c>COUNT</c> and
    /// is left alone).</description></item>
    /// <item><description><c>SUM</c> over any integral column returns <c>BIGINT</c>, and over a
    /// <c>FLOAT</c> column returns <c>DOUBLE</c>, but <c>Sum</c> is typed after its
    /// selector.</description></item>
    /// </list>
    /// Narrowing in SQL keeps the aggregate's semantics (<c>DISTINCT</c>, predicates and selectors
    /// are all still produced by the shared translator) while giving EF the type it expects.
    /// </remarks>
    protected override Expression VisitSqlFunction(SqlFunctionExpression sqlFunctionExpression)
    {
        var narrowTo = NarrowedAggregateStoreType(sqlFunctionExpression);
        if (narrowTo is not null)
        {
            Sql.Append("CAST(");
            base.VisitSqlFunction(sqlFunctionExpression);
            Sql.Append(" AS ").Append(narrowTo).Append(")");
            return sqlFunctionExpression;
        }

        return base.VisitSqlFunction(sqlFunctionExpression);
    }

    /// <summary>
    /// The store type an aggregate's result must be narrowed to, or <see langword="null" /> when
    /// Databricks already returns the type EF expects.
    /// </summary>
    private static string? NarrowedAggregateStoreType(SqlFunctionExpression function)
    {
        var clrType = Nullable.GetUnderlyingType(function.Type) ?? function.Type;

        if (string.Equals(function.Name, "COUNT", StringComparison.OrdinalIgnoreCase))
        {
            return clrType == typeof(int) ? "INT" : null;
        }

        if (string.Equals(function.Name, "SUM", StringComparison.OrdinalIgnoreCase))
        {
            return clrType == typeof(int) ? "INT"
                : clrType == typeof(float) ? "FLOAT"
                : null;
        }

        return null;
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
