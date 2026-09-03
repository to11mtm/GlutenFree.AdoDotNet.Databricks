using System.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// <c>STRING</c> mapping that renders literals the way Spark SQL parses them.
/// </summary>
/// <remarks>
/// The relational default escapes an embedded quote by doubling it (<c>''</c>). Spark SQL
/// instead uses backslash escaping, and reads <c>''</c> as two adjacent literals concatenated —
/// so the default silently *drops* the quote rather than escaping it. Backslashes must be
/// escaped for the same reason. This matches the literal rules already proven by the linq2db
/// provider's <c>DatabricksMappingSchema</c>.
/// </remarks>
public class DatabricksStringTypeMapping : StringTypeMapping
{
    /// <summary>Creates the mapping.</summary>
    public DatabricksStringTypeMapping()
        : base("STRING", System.Data.DbType.String, unicode: true)
    {
    }

    /// <summary>Copy constructor used by <see cref="Clone(RelationalTypeMappingParameters)" />.</summary>
    protected DatabricksStringTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new DatabricksStringTypeMapping(parameters);

    /// <inheritdoc />
    protected override string GenerateNonNullSqlLiteral(object value)
    {
        // char arrives here when the char mapping's converter has not already widened it.
        var text = value as string ?? value.ToString()!;

        return "'" + EscapeLiteral(text) + "'";
    }

    /// <summary>Escapes a string for a Spark SQL single-quoted literal.</summary>
    internal static string EscapeLiteral(string value)
        => value
            // Backslash first, so the quote escapes added below are not themselves escaped.
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
}
