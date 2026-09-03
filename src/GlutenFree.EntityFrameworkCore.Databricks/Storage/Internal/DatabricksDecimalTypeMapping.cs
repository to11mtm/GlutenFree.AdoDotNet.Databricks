using GlutenFree.Databricks.AdoNet;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// Maps <see cref="DatabricksDecimal" /> — an arbitrary-precision BigDecimal — onto
/// <c>DECIMAL(p, s)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Databricks allows up to <c>DECIMAL(38, s)</c>, which is more precision than .NET's
/// <see cref="decimal" /> can hold (~28 significant digits). Reading such a column into a
/// <see cref="decimal" /> overflows, so <see cref="DatabricksDecimal" /> is the lossless model
/// type for wide columns.
/// </para>
/// <para>
/// No value converter is involved: the ADO.NET layer already round-trips the type directly —
/// <c>DatabricksDataReader.GetFieldValue&lt;DatabricksDecimal&gt;</c> reads it whatever the
/// wire representation, and <c>DatabricksParameter</c> binds it with an exact
/// <c>DECIMAL(p, s)</c> type without narrowing.
/// </para>
/// <para>
/// The default is <c>DECIMAL(38, 18)</c> rather than deferring to the server: Databricks'
/// own default is <c>DECIMAL(10, 0)</c>, which would silently truncate.
/// </para>
/// </remarks>
public class DatabricksDecimalTypeMapping : RelationalTypeMapping
{
    /// <summary>Databricks' maximum <c>DECIMAL</c> precision.</summary>
    public const int MaxPrecision = 38;

    private const int DefaultScale = 18;

    /// <summary>Creates the mapping with the default precision and scale.</summary>
    public DatabricksDecimalTypeMapping()
        : base(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(DatabricksDecimal)),
                "DECIMAL",
                StoreTypePostfix.PrecisionAndScale,
                System.Data.DbType.Decimal,
                precision: MaxPrecision,
                scale: DefaultScale))
    {
    }

    /// <summary>Copy constructor used by <see cref="Clone(RelationalTypeMappingParameters)" />.</summary>
    protected DatabricksDecimalTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new DatabricksDecimalTypeMapping(parameters);

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="DatabricksDecimal.ToString()" /> is already the invariant canonical form
    /// (trailing zeros preserved), which is what Spark parses as a <c>DECIMAL</c> literal.
    /// </remarks>
    protected override string GenerateNonNullSqlLiteral(object value)
        => value.ToString()!;
}
