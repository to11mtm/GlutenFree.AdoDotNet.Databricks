using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// Maps <see cref="char" /> to a one-character <c>STRING</c>.
/// </summary>
/// <remarks>
/// Databricks has no character type distinct from <c>STRING</c>. The converter widens the value
/// before it reaches a parameter, and literals use the same Spark escaping rules as
/// <see cref="DatabricksStringTypeMapping" />.
/// </remarks>
public class DatabricksCharTypeMapping : RelationalTypeMapping
{
    /// <summary>Creates the mapping.</summary>
    public DatabricksCharTypeMapping()
        : base(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(char), new CharToStringConverter()),
                "STRING",
                StoreTypePostfix.None,
                System.Data.DbType.String,
                unicode: true))
    {
    }

    /// <summary>Copy constructor used by <see cref="Clone(RelationalTypeMappingParameters)" />.</summary>
    protected DatabricksCharTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new DatabricksCharTypeMapping(parameters);

    /// <inheritdoc />
    protected override string GenerateNonNullSqlLiteral(object value)
        => "'" + DatabricksStringTypeMapping.EscapeLiteral(value.ToString()!) + "'";
}
