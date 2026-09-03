using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>Translates <see cref="string.Length" /> to Databricks' <c>length</c>.</summary>
public class DatabricksStringMemberTranslator(ISqlExpressionFactory sqlExpressionFactory) : IMemberTranslator
{
    /// <inheritdoc />
    public virtual SqlExpression? Translate(
        SqlExpression? instance,
        MemberInfo member,
        Type returnType,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        => instance is not null
            && member.DeclaringType == typeof(string)
            && member.Name == nameof(string.Length)
                ? sqlExpressionFactory.Function(
                    "length",
                    [instance],
                    nullable: true,
                    argumentsPropagateNullability: [true],
                    typeof(int))
                : null;
}
