using System.Data;
using Microsoft.EntityFrameworkCore.Storage;
namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// <c>BOOLEAN</c> mapping that renders literals as <c>TRUE</c>/<c>FALSE</c>.
/// </summary>
/// <remarks>
/// The relational default renders <c>1</c>/<c>0</c>, which Databricks does not implicitly
/// convert to <c>BOOLEAN</c> in every position.
/// </remarks>
public class DatabricksBoolTypeMapping : BoolTypeMapping
{
    /// <summary>Creates the mapping.</summary>
    public DatabricksBoolTypeMapping()
        : base("BOOLEAN", System.Data.DbType.Boolean)
    {
    }

    /// <summary>Copy constructor used by <see cref="Clone(RelationalTypeMappingParameters)" />.</summary>
    protected DatabricksBoolTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new DatabricksBoolTypeMapping(parameters);

    /// <inheritdoc />
    protected override string GenerateNonNullSqlLiteral(object value)
        => (bool)value ? "TRUE" : "FALSE";
}
