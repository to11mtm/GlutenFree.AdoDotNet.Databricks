using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>
/// Translates date and time component members to the corresponding Databricks functions.
/// </summary>
public class DatabricksDateTimeMemberTranslator(ISqlExpressionFactory sqlExpressionFactory) : IMemberTranslator
{
    /// <summary>Members that map directly onto a single-argument Databricks function.</summary>
    private static readonly Dictionary<string, string> ComponentFunctions = new(StringComparer.Ordinal)
    {
        [nameof(DateTime.Year)] = "year",
        [nameof(DateTime.Month)] = "month",
        [nameof(DateTime.Day)] = "dayofmonth",
        [nameof(DateTime.Hour)] = "hour",
        [nameof(DateTime.Minute)] = "minute",
        [nameof(DateTime.Second)] = "second",
        [nameof(DateTime.DayOfYear)] = "dayofyear",
    };

    /// <inheritdoc />
    public virtual SqlExpression? Translate(
        SqlExpression? instance,
        MemberInfo member,
        Type returnType,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        var declaringType = member.DeclaringType;

        if (declaringType == typeof(DateTime) || declaringType == typeof(DateTimeOffset) || declaringType == typeof(DateOnly))
        {
            if (instance is not null && ComponentFunctions.TryGetValue(member.Name, out var function))
            {
                return Function(function, typeof(int), instance);
            }

            if (instance is not null && member.Name == nameof(DateTime.Date))
            {
                // Truncating to the day keeps the value a timestamp, matching DateTime.Date.
                return Function("date_trunc", returnType, sqlExpressionFactory.Constant("DAY"), instance);
            }

            if (instance is not null && member.Name == nameof(DateTime.DayOfWeek))
            {
                // Databricks' dayofweek is 1-based starting on Sunday; DayOfWeek is 0-based
                // starting on Sunday.
                return sqlExpressionFactory.Subtract(
                    Function("dayofweek", typeof(int), instance),
                    sqlExpressionFactory.Constant(1));
            }

            if (instance is null)
            {
                switch (member.Name)
                {
                    case nameof(DateTime.Now):
                        return Function("current_timestamp", returnType);
                    case nameof(DateTime.UtcNow):
                        // current_timestamp() is session-zone based; normalize to UTC.
                        return Function(
                            "to_utc_timestamp",
                            returnType,
                            Function("current_timestamp", returnType),
                            sqlExpressionFactory.Constant("UTC"));
                    case nameof(DateTime.Today):
                        return Function("current_date", returnType);
                }
            }
        }

        return null;
    }

    private SqlExpression Function(string name, Type returnType, params SqlExpression[] arguments)
        => sqlExpressionFactory.Function(
            name,
            arguments,
            nullable: true,
            argumentsPropagateNullability: arguments.Select(_ => true).ToArray(),
            returnType);
}
