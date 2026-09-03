using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>Translates <see cref="Math" /> methods to Databricks SQL functions.</summary>
public class DatabricksMathTranslator(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslator
{
    /// <summary>
    /// <see cref="Math" /> methods whose Databricks counterpart takes the same arguments and
    /// returns the same type as the CLR method.
    /// </summary>
    private static readonly Dictionary<string, string> DirectFunctions = new(StringComparer.Ordinal)
    {
        [nameof(Math.Abs)] = "abs",
        [nameof(Math.Ceiling)] = "ceil",
        [nameof(Math.Floor)] = "floor",
        [nameof(Math.Exp)] = "exp",
        [nameof(Math.Log10)] = "log10",
        [nameof(Math.Sqrt)] = "sqrt",
        [nameof(Math.Sin)] = "sin",
        [nameof(Math.Cos)] = "cos",
        [nameof(Math.Tan)] = "tan",
        [nameof(Math.Asin)] = "asin",
        [nameof(Math.Acos)] = "acos",
        [nameof(Math.Atan)] = "atan",
        [nameof(Math.Atan2)] = "atan2",
        [nameof(Math.Pow)] = "power",
        [nameof(Math.Round)] = "round",
        [nameof(Math.Truncate)] = "trunc",
    };

    /// <inheritdoc />
    public virtual SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(Math) && method.DeclaringType != typeof(MathF))
        {
            return null;
        }

        if (method.Name == nameof(Math.Sign))
        {
            // Databricks' signum returns a floating-point value; Math.Sign returns int.
            return sqlExpressionFactory.Convert(
                Function("signum", typeof(double), arguments), typeof(int));
        }

        if (method.Name == nameof(Math.Log) && arguments.Count == 1)
        {
            return Function("ln", method.ReturnType, arguments);
        }

        return DirectFunctions.TryGetValue(method.Name, out var function)
            ? Function(function, method.ReturnType, arguments)
            : null;
    }

    private SqlExpression Function(string name, Type returnType, IReadOnlyList<SqlExpression> arguments)
        => sqlExpressionFactory.Function(
            name,
            arguments,
            nullable: true,
            argumentsPropagateNullability: arguments.Select(_ => true).ToArray(),
            returnType);
}
