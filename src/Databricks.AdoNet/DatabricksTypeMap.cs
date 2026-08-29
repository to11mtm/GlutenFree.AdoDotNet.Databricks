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
            // The Statement Execution API delivers ARRAY/MAP/STRUCT as genuine Arrow nested
            // arrays; v1 surfaces them as JSON strings per the type-mapping spec.
            ListArray or StructArray => SerializeNestedToJson(array, index),
            _ => throw new NotSupportedException(
                $"Arrow array type '{array.GetType().Name}' (column '{column.Name}', " +
                $"type '{column.TypeText ?? column.TypeName}') is not supported."),
        };
    }

    private static string SerializeNestedToJson(IArrowArray array, int index)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            WriteArrowJsonValue(writer, array, index);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteArrowJsonValue(System.Text.Json.Utf8JsonWriter writer, IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            writer.WriteNullValue();
            return;
        }

        switch (array)
        {
            case BooleanArray a:
                writer.WriteBooleanValue(a.GetValue(index)!.Value);
                break;
            case Int8Array a:
                writer.WriteNumberValue(a.GetValue(index)!.Value);
                break;
            case Int16Array a:
                writer.WriteNumberValue(a.GetValue(index)!.Value);
                break;
            case Int32Array a:
                writer.WriteNumberValue(a.GetValue(index)!.Value);
                break;
            case Int64Array a:
                writer.WriteNumberValue(a.GetValue(index)!.Value);
                break;
            case FloatArray a:
                writer.WriteNumberValue(a.GetValue(index)!.Value);
                break;
            case DoubleArray a:
                writer.WriteNumberValue(a.GetValue(index)!.Value);
                break;
            case Decimal128Array a:
                writer.WriteRawValue(a.GetString(index), skipInputValidation: false);
                break;
            case Date32Array a:
                writer.WriteStringValue(a.GetDateOnly(index)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimestampArray a:
                writer.WriteStringValue(a.GetTimestamp(index)!.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                break;
            case StringArray a:
                writer.WriteStringValue(a.GetString(index));
                break;
            case BinaryArray a:
                writer.WriteBase64StringValue(a.GetBytes(index));
                break;
            // MapArray derives from ListArray; match it first.
            case MapArray m:
            {
                var start = m.ValueOffsets[index];
                var end = m.ValueOffsets[index + 1];
                var entries = (StructArray)m.Values;
                var keys = entries.Fields[0];
                var values = entries.Fields[1];
                writer.WriteStartObject();
                for (var i = start; i < end; i++)
                {
                    writer.WritePropertyName(GetJsonPropertyName(keys, i));
                    WriteArrowJsonValue(writer, values, i);
                }

                writer.WriteEndObject();
                break;
            }

            case ListArray l:
            {
                var start = l.ValueOffsets[index];
                var end = l.ValueOffsets[index + 1];
                writer.WriteStartArray();
                for (var i = start; i < end; i++)
                {
                    WriteArrowJsonValue(writer, l.Values, i);
                }

                writer.WriteEndArray();
                break;
            }

            case StructArray s:
            {
                // Struct children are not offset-adjusted by arrow-dotnet; apply parent offset.
                var childIndex = index + s.Offset;
                var fields = ((Apache.Arrow.Types.StructType)s.Data.DataType).Fields;
                writer.WriteStartObject();
                for (var i = 0; i < fields.Count; i++)
                {
                    writer.WritePropertyName(fields[i].Name);
                    WriteArrowJsonValue(writer, s.Fields[i], childIndex);
                }

                writer.WriteEndObject();
                break;
            }

            default:
                writer.WriteStringValue(array.GetType().Name);
                break;
        }
    }

    private static string GetJsonPropertyName(IArrowArray keys, int index) => keys switch
    {
        StringArray a => a.GetString(index),
        Int8Array a => a.GetValue(index)!.Value.ToString(CultureInfo.InvariantCulture),
        Int16Array a => a.GetValue(index)!.Value.ToString(CultureInfo.InvariantCulture),
        Int32Array a => a.GetValue(index)!.Value.ToString(CultureInfo.InvariantCulture),
        Int64Array a => a.GetValue(index)!.Value.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(
                keys is BooleanArray b ? b.GetValue(index) : "key", CultureInfo.InvariantCulture)
            ?? "key",
    };
}
