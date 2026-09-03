using System.Collections.Generic;
using GlutenFree.Databricks.AdoNet;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// Maps CLR types to Databricks SQL store types, matching the mappings the ADO.NET provider's
/// reader produces (<c>DatabricksTypeMap</c>) and the linq2db provider's DDL type names.
/// </summary>
/// <remarks>
/// Notes on the Databricks type system:
/// <list type="bullet">
/// <item><description><c>STRING</c> has no length facet, so string columns are never sized.</description></item>
/// <item><description>
/// <c>TIMESTAMP</c> is zone-aware (normalized to UTC) and <c>TIMESTAMP_NTZ</c> is not, so
/// <see cref="DateTimeOffset" /> maps to the former and <see cref="DateTime" /> to the latter.
/// </description></item>
/// <item><description>
/// There is no native <c>GUID</c>/<c>UUID</c> type; <see cref="Guid" /> is stored as
/// <c>STRING</c>, matching the linq2db provider.
/// </description></item>
/// <item><description>
/// The default <c>DECIMAL</c> precision/scale follows Databricks' own default of
/// <c>DECIMAL(10, 0)</c> only when the model gives no facets; models should specify them.
/// </description></item>
/// </list>
/// </remarks>
public class DatabricksTypeMappingSource : RelationalTypeMappingSource
{
    private const int MaxDecimalPrecision = 38;

    private readonly BoolTypeMapping _boolean = new DatabricksBoolTypeMapping();
    private readonly SByteTypeMapping _tinyInt = new("TINYINT", System.Data.DbType.SByte);
    private readonly ShortTypeMapping _smallInt = new("SMALLINT", System.Data.DbType.Int16);
    private readonly IntTypeMapping _int = new("INT", System.Data.DbType.Int32);
    private readonly LongTypeMapping _bigInt = new("BIGINT", System.Data.DbType.Int64);
    private readonly FloatTypeMapping _float = new("FLOAT", System.Data.DbType.Single);
    private readonly DoubleTypeMapping _double = new("DOUBLE", System.Data.DbType.Double);
    private readonly DecimalTypeMapping _decimal = new($"DECIMAL({MaxDecimalPrecision}, 18)", System.Data.DbType.Decimal);
    private readonly RelationalTypeMapping _bigDecimal = new DatabricksDecimalTypeMapping();
    private readonly DateOnlyTypeMapping _date = new("DATE", System.Data.DbType.Date);
    private readonly DateTimeTypeMapping _timestampNtz = new("TIMESTAMP_NTZ", System.Data.DbType.DateTime2);
    private readonly DateTimeOffsetTypeMapping _timestamp = new("TIMESTAMP", System.Data.DbType.DateTimeOffset);
    private readonly StringTypeMapping _string = new DatabricksStringTypeMapping();
    private readonly ByteArrayTypeMapping _binary = new("BINARY", System.Data.DbType.Binary);
    private readonly GuidTypeMapping _guid = new("STRING", System.Data.DbType.String);
    private readonly TimeSpanTypeMapping _interval = new("INTERVAL DAY TO SECOND", System.Data.DbType.Time);

    private readonly RelationalTypeMapping _char = new DatabricksCharTypeMapping();

    private readonly Dictionary<Type, RelationalTypeMapping> _clrTypeMappings;
    private readonly Dictionary<string, RelationalTypeMapping> _storeTypeMappings;

    /// <summary>Creates the type mapping source.</summary>
    public DatabricksTypeMappingSource(
        TypeMappingSourceDependencies dependencies,
        RelationalTypeMappingSourceDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
        _clrTypeMappings = new Dictionary<Type, RelationalTypeMapping>
        {
            [typeof(bool)] = _boolean,
            [typeof(sbyte)] = _tinyInt,
            // Databricks has no unsigned types; byte widens to the smallest type that holds it.
            [typeof(byte)] = _smallInt,
            [typeof(short)] = _smallInt,
            [typeof(ushort)] = _int,
            [typeof(int)] = _int,
            [typeof(uint)] = _bigInt,
            [typeof(long)] = _bigInt,
            [typeof(ulong)] = _decimal,
            [typeof(float)] = _float,
            [typeof(double)] = _double,
            [typeof(decimal)] = _decimal,
            [typeof(DatabricksDecimal)] = _bigDecimal,
            [typeof(DateOnly)] = _date,
            [typeof(DateTime)] = _timestampNtz,
            [typeof(DateTimeOffset)] = _timestamp,
            [typeof(TimeSpan)] = _interval,
            [typeof(string)] = _string,
            [typeof(char)] = _char,
            [typeof(byte[])] = _binary,
            [typeof(Guid)] = _guid,
        };

        _storeTypeMappings = new Dictionary<string, RelationalTypeMapping>(StringComparer.OrdinalIgnoreCase)
        {
            ["BOOLEAN"] = _boolean,
            ["TINYINT"] = _tinyInt,
            ["BYTE"] = _tinyInt,
            ["SMALLINT"] = _smallInt,
            ["SHORT"] = _smallInt,
            ["INT"] = _int,
            ["INTEGER"] = _int,
            ["BIGINT"] = _bigInt,
            ["LONG"] = _bigInt,
            ["FLOAT"] = _float,
            ["REAL"] = _float,
            ["DOUBLE"] = _double,
            ["DECIMAL"] = _decimal,
            ["DEC"] = _decimal,
            ["NUMERIC"] = _decimal,
            ["DATE"] = _date,
            ["TIMESTAMP"] = _timestamp,
            ["TIMESTAMP_NTZ"] = _timestampNtz,
            ["STRING"] = _string,
            ["BINARY"] = _binary,
        };
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
        => FindRawMapping(mappingInfo)?.WithTypeMappingInfo(mappingInfo)
            ?? base.FindMapping(mappingInfo);

    private RelationalTypeMapping? FindRawMapping(RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType;
        var storeTypeName = mappingInfo.StoreTypeNameBase ?? mappingInfo.StoreTypeName;

        if (storeTypeName is not null)
        {
            if (_storeTypeMappings.TryGetValue(storeTypeName, out var mapping)
                && (clrType is null || mapping.ClrType == clrType))
            {
                return mapping;
            }

            // CHAR/VARCHAR are accepted for portability; Databricks stores both with STRING
            // semantics, and the length facet is not enforced through the mapping layer.
            if (IsStringStoreType(storeTypeName) && (clrType is null || clrType == typeof(string)))
            {
                return _string;
            }
        }

        if (clrType is not null && _clrTypeMappings.TryGetValue(clrType, out var clrMapping))
        {
            return clrMapping;
        }

        return null;
    }

    private static bool IsStringStoreType(string storeTypeName)
        => storeTypeName.Equals("CHAR", StringComparison.OrdinalIgnoreCase)
            || storeTypeName.Equals("VARCHAR", StringComparison.OrdinalIgnoreCase);
}
