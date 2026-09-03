using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>
/// Translates date and time arithmetic (<c>AddDays</c>, <c>AddMonths</c>, …) to Databricks'
/// <c>timestampadd(unit, value, expr)</c>.
/// </summary>
/// <remarks>
/// The CLR overloads take <see cref="double" /> while <c>timestampadd</c> wants an integral
/// amount, so the interval is cast to <c>INT</c>. That means fractional amounts (for example
/// <c>AddDays(1.5)</c>) are not supported and are left untranslated rather than silently
/// truncated server-side.
/// </remarks>
public class DatabricksDateTimeMethodTranslator(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslator
{
    /// <summary>Method name to <c>timestampadd</c> unit.</summary>
    private static readonly Dictionary<string, string> AddMethods = new(StringComparer.Ordinal)
    {
        [nameof(DateTime.AddYears)] = "YEAR",
        [nameof(DateTime.AddMonths)] = "MONTH",
        [nameof(DateTime.AddDays)] = "DAY",
        [nameof(DateTime.AddHours)] = "HOUR",
        [nameof(DateTime.AddMinutes)] = "MINUTE",
        [nameof(DateTime.AddSeconds)] = "SECOND",
        [nameof(DateTime.AddMilliseconds)] = "MILLISECOND",
    };

    private static readonly HashSet<Type> SupportedTypes =
    [
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
    ];

    /// <inheritdoc />
    public virtual SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance is null
            || method.DeclaringType is null
            || !SupportedTypes.Contains(method.DeclaringType)
            || arguments.Count != 1
            || !AddMethods.TryGetValue(method.Name, out var unit))
        {
            return null;
        }

        // Fractional intervals cannot be expressed; leave them to fail loudly rather than
        // rounding behind the caller's back.
        if (arguments[0] is SqlConstantExpression { Value: double d } && d != Math.Floor(d))
        {
            return null;
        }

        var amount = arguments[0].Type == typeof(int)
            ? arguments[0]
            : sqlExpressionFactory.Convert(arguments[0], typeof(int));

        return sqlExpressionFactory.Function(
            "timestampadd",
            [sqlExpressionFactory.Fragment(unit), amount, instance],
            nullable: true,
            argumentsPropagateNullability: [false, true, true],
            instance.Type,
            instance.TypeMapping);
    }
}
