using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>
/// Translates <see cref="string" /> methods to Databricks SQL functions.
/// </summary>
/// <remarks>
/// Databricks has native <c>startswith</c>/<c>endswith</c>/<c>contains</c> predicates, so those
/// are used in preference to <c>LIKE</c> with pattern escaping.
/// </remarks>
public class DatabricksStringMethodTranslator(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslator
{
    private static readonly MethodInfo StartsWith =
        typeof(string).GetRuntimeMethod(nameof(string.StartsWith), [typeof(string)])!;

    private static readonly MethodInfo EndsWith =
        typeof(string).GetRuntimeMethod(nameof(string.EndsWith), [typeof(string)])!;

    private static readonly MethodInfo Contains =
        typeof(string).GetRuntimeMethod(nameof(string.Contains), [typeof(string)])!;

    private static readonly MethodInfo ToUpper =
        typeof(string).GetRuntimeMethod(nameof(string.ToUpper), [])!;

    private static readonly MethodInfo ToLower =
        typeof(string).GetRuntimeMethod(nameof(string.ToLower), [])!;

    private static readonly MethodInfo Trim =
        typeof(string).GetRuntimeMethod(nameof(string.Trim), [])!;

    private static readonly MethodInfo TrimStart =
        typeof(string).GetRuntimeMethod(nameof(string.TrimStart), [])!;

    private static readonly MethodInfo TrimEnd =
        typeof(string).GetRuntimeMethod(nameof(string.TrimEnd), [])!;

    private static readonly MethodInfo Replace =
        typeof(string).GetRuntimeMethod(nameof(string.Replace), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo IndexOf =
        typeof(string).GetRuntimeMethod(nameof(string.IndexOf), [typeof(string)])!;

    private static readonly MethodInfo SubstringFrom =
        typeof(string).GetRuntimeMethod(nameof(string.Substring), [typeof(int)])!;

    private static readonly MethodInfo SubstringRange =
        typeof(string).GetRuntimeMethod(nameof(string.Substring), [typeof(int), typeof(int)])!;

    private static readonly MethodInfo IsNullOrEmpty =
        typeof(string).GetRuntimeMethod(nameof(string.IsNullOrEmpty), [typeof(string)])!;

    private static readonly MethodInfo IsNullOrWhiteSpace =
        typeof(string).GetRuntimeMethod(nameof(string.IsNullOrWhiteSpace), [typeof(string)])!;

    /// <inheritdoc />
    public virtual SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance is not null)
        {
            if (method == StartsWith)
            {
                return BoolFunction("startswith", instance, arguments[0]);
            }

            if (method == EndsWith)
            {
                return BoolFunction("endswith", instance, arguments[0]);
            }

            if (method == Contains)
            {
                return BoolFunction("contains", instance, arguments[0]);
            }

            if (method == ToUpper)
            {
                return StringFunction("upper", instance);
            }

            if (method == ToLower)
            {
                return StringFunction("lower", instance);
            }

            if (method == Trim)
            {
                return StringFunction("trim", instance);
            }

            if (method == TrimStart)
            {
                return StringFunction("ltrim", instance);
            }

            if (method == TrimEnd)
            {
                return StringFunction("rtrim", instance);
            }

            if (method == Replace)
            {
                return StringFunction("replace", instance, arguments[0], arguments[1]);
            }

            if (method == IndexOf)
            {
                // Databricks' locate() is 1-based and returns 0 when not found; IndexOf is
                // 0-based and returns -1, so both cases are covered by subtracting one.
                return sqlExpressionFactory.Subtract(
                    Function("locate", typeof(int), arguments[0], instance),
                    sqlExpressionFactory.Constant(1));
            }

            if (method == SubstringFrom)
            {
                // substring() is 1-based; the CLR overload takes a 0-based start index.
                return StringFunction(
                    "substring",
                    instance,
                    sqlExpressionFactory.Add(arguments[0], sqlExpressionFactory.Constant(1)));
            }

            if (method == SubstringRange)
            {
                return StringFunction(
                    "substring",
                    instance,
                    sqlExpressionFactory.Add(arguments[0], sqlExpressionFactory.Constant(1)),
                    arguments[1]);
            }
        }

        if (method == IsNullOrEmpty)
        {
            var argument = arguments[0];
            return sqlExpressionFactory.OrElse(
                sqlExpressionFactory.IsNull(argument),
                sqlExpressionFactory.Equal(argument, sqlExpressionFactory.Constant(string.Empty)));
        }

        if (method == IsNullOrWhiteSpace)
        {
            var argument = arguments[0];
            return sqlExpressionFactory.OrElse(
                sqlExpressionFactory.IsNull(argument),
                sqlExpressionFactory.Equal(
                    StringFunction("trim", argument),
                    sqlExpressionFactory.Constant(string.Empty)));
        }

        return null;
    }

    private SqlExpression BoolFunction(string name, params SqlExpression[] arguments)
        => Function(name, typeof(bool), arguments);

    private SqlExpression StringFunction(string name, params SqlExpression[] arguments)
        => Function(name, typeof(string), arguments);

    private SqlExpression Function(string name, Type returnType, params SqlExpression[] arguments)
        => sqlExpressionFactory.Function(
            name,
            arguments,
            nullable: true,
            argumentsPropagateNullability: arguments.Select(_ => true).ToArray(),
            returnType);
}
