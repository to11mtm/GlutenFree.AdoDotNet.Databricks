using System.Data.SqlTypes;
using System.Globalization;
using Apache.Arrow;
using Databricks.AdoNet.Transport;

namespace Databricks.AdoNet;

/// <summary>
/// Maps Databricks SQL types to .NET types and converts raw wire values
/// (JSON strings or Arrow array slots) into .NET values.
/// </summary>
internal static class DatabricksTypeMap
{
    /// <summary>Maximum DECIMAL precision representable by <see cref="decimal"/> without overflow risk.</summary>
    internal const int MaxNetDecimalPrecision = 28;

    internal static Type GetFieldType(ColumnInfo column) => Normalize(column.TypeName) switch
    {
        "BOOLEAN" => typeof(bool),
        "TINYINT" => typeof(sbyte),
        "SMALLINT" => typeof(short),
        "INT" => typeof(int),
        "BIGINT" => typeof(long),
        "FLOAT" => typeof(float),
        "DOUBLE" => typeof(double),
        "DECIMAL" => column.TypePrecision > MaxNetDecimalPrecision ? typeof(SqlDecimal) : typeof(decimal),
        "DATE" => typeof(DateOnly),
        "TIMESTAMP" or "TIMESTAMP_NTZ" => typeof(DateTime),
        "BINARY" => typeof(byte[]),
        // STRING, CHAR, INTERVAL, ARRAY, MAP, STRUCT, VARIANT, NULL and anything else.
        _ => typeof(string),
    };

    /// <summary>Normalizes Spark/Databricks type-name aliases to canonical SQL names.</summary>
    internal static string Normalize(string? typeName) => typeName?.ToUpperInvariant() switch
    {
        "BYTE" => "TINYINT",
        "SHORT" => "SMALLINT",
        "INTEGER" => "INT",
        "LONG" => "BIGINT",
        "REAL" => "FLOAT",
        null => "STRING",
        var other => other,
    };

    /// <summary>Converts a raw JSON_ARRAY cell (string or null) into a .NET value.</summary>
    internal static object ConvertJsonValue(string? raw, ColumnInfo column)
    {
        if (raw is null)
        {
            return DBNull.Value;
        }

        return Normalize(column.TypeName) switch
        {
            "BOOLEAN" => bool.Parse(raw),
            "TINYINT" => sbyte.Parse(raw, CultureInfo.InvariantCulture),
            "SMALLINT" => short.Parse(raw, CultureInfo.InvariantCulture),
            "INT" => int.Parse(raw, CultureInfo.InvariantCulture),
            "BIGINT" => long.Parse(raw, CultureInfo.InvariantCulture),
            "FLOAT" => float.Parse(raw, CultureInfo.InvariantCulture),
            "DOUBLE" => double.Parse(raw, CultureInfo.InvariantCulture),
            "DECIMAL" when column.TypePrecision > MaxNetDecimalPrecision => SqlDecimal.Parse(raw),
            "DECIMAL" => decimal.Parse(raw, CultureInfo.InvariantCulture),
            "DATE" => DateOnly.Parse(raw, CultureInfo.InvariantCulture),
            "TIMESTAMP" or "TIMESTAMP_NTZ" => DateTime.Parse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            "BINARY" => Convert.FromBase64String(raw),
            _ => raw,
        };
    }

    /// <summary>Converts an Arrow array slot into a .NET value.</summary>
    internal static object ConvertArrowValue(IArrowArray array, int index, ColumnInfo column)
    {
        if (array.IsNull(index))
        {
            return DBNull.Value;
        }

        return array switch
        {
            BooleanArray a => a.GetValue(index)!.Value,
            Int8Array a => a.GetValue(index)!.Value,
            Int16Array a => a.GetValue(index)!.Value,
            Int32Array a => a.GetValue(index)!.Value,
            Int64Array a => a.GetValue(index)!.Value,
            FloatArray a => a.GetValue(index)!.Value,
            DoubleArray a => a.GetValue(index)!.Value,
            Decimal128Array a => column.TypePrecision > MaxNetDecimalPrecision
                ? a.GetSqlDecimal(index)!.Value
                : a.GetValue(index)!.Value,
            Date32Array a => a.GetDateOnly(index)!.Value,
            Date64Array a => a.GetDateOnly(index)!.Value,
            TimestampArray a => a.GetTimestamp(index)!.Value.UtcDateTime,
            StringArray a => a.GetString(index),
            BinaryArray a => a.GetBytes(index).ToArray(),
            _ => throw new NotSupportedException(
                $"Arrow array type '{array.GetType().Name}' (column '{column.Name}', " +
                $"type '{column.TypeText ?? column.TypeName}') is not supported."),
        };
    }
}
