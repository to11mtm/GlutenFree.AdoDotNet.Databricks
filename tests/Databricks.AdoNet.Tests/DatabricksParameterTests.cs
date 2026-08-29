using System.Data;
using Databricks.AdoNet.Transport;

namespace Databricks.AdoNet.Tests;

public class DatabricksParameterTests
{
    private static StatementParameter Convert(object? value)
        => new DatabricksParameter("p", value).ToStatementParameter();

    [Theory]
    [InlineData(true, "TRUE", "BOOLEAN")]
    [InlineData(false, "FALSE", "BOOLEAN")]
    [InlineData((sbyte)5, "5", "TINYINT")]
    [InlineData((short)-3, "-3", "SMALLINT")]
    [InlineData(42, "42", "INT")]
    [InlineData(42L, "42", "BIGINT")]
    [InlineData(1.5f, "1.5", "FLOAT")]
    [InlineData(2.25, "2.25", "DOUBLE")]
    [InlineData("text", "text", "STRING")]
    public void Infers_wire_types_from_net_values(object value, string expectedValue, string expectedType)
    {
        var parameter = Convert(value);
        Assert.Equal(expectedValue, parameter.Value);
        Assert.Equal(expectedType, parameter.Type);
    }

    [Fact]
    public void Decimal_carries_precision_and_scale()
    {
        var parameter = Convert(123.450m);
        Assert.Equal("123.450", parameter.Value);
        Assert.Equal("DECIMAL(6,3)", parameter.Type);
    }

    [Fact]
    public void DateTime_kind_selects_timestamp_type()
    {
        var utc = Convert(new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal("TIMESTAMP", utc.Type);
        Assert.Equal("2026-08-29 12:00:00.000000", utc.Value);

        var unspecified = Convert(new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal("TIMESTAMP_NTZ", unspecified.Type);
    }

    [Fact]
    public void DateTimeOffset_preserves_offset()
    {
        var parameter = Convert(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(-4)));
        Assert.Equal("2026-08-29 12:00:00.000000-04:00", parameter.Value);
        Assert.Equal("TIMESTAMP", parameter.Type);
    }

    [Fact]
    public void Guid_is_sent_as_string()
    {
        var guid = Guid.NewGuid();
        var parameter = Convert(guid);
        Assert.Equal(guid.ToString("D"), parameter.Value);
        Assert.Equal("STRING", parameter.Type);
    }

    [Fact]
    public void Null_uses_DbType_for_typed_null()
    {
        var parameter = new DatabricksParameter("p", null) { DbType = DbType.Int64 };
        var wire = parameter.ToStatementParameter();
        Assert.Null(wire.Value);
        Assert.Equal("BIGINT", wire.Type);

        Assert.Null(Convert(DBNull.Value).Value);
    }

    [Fact]
    public void Byte_array_throws_not_supported()
    {
        Assert.Throws<NotSupportedException>(() => Convert(new byte[] { 1, 2 }));
    }

    [Fact]
    public void Unsupported_type_throws()
    {
        Assert.Throws<NotSupportedException>(() => Convert(new object()));
    }

    [Fact]
    public void Unnamed_parameter_throws_at_conversion()
    {
        var parameter = new DatabricksParameter { Value = 1 };
        Assert.Throws<InvalidOperationException>(parameter.ToStatementParameter);
    }

    [Fact]
    public void ParameterName_strips_marker_prefix()
    {
        Assert.Equal("id", new DatabricksParameter(":id", 1).ParameterName);
    }
}

public class DatabricksParameterCollectionTests
{
    [Fact]
    public void IndexOf_is_case_insensitive_and_prefix_tolerant()
    {
        var collection = new DatabricksParameterCollection();
        collection.AddWithValue("Alpha", 1);
        collection.AddWithValue("beta", 2);

        Assert.Equal(0, collection.IndexOf("alpha"));
        Assert.Equal(1, collection.IndexOf(":BETA"));
        Assert.True(collection.Contains("ALPHA"));
        Assert.Equal(-1, collection.IndexOf("gamma"));
    }

    [Fact]
    public void Indexer_by_name_gets_and_replaces()
    {
        var collection = new DatabricksParameterCollection();
        collection.AddWithValue("a", 1);

        Assert.Equal(1, ((DatabricksParameter)collection["a"]).Value);

        collection["a"] = new DatabricksParameter("a", 99);
        Assert.Equal(99, ((DatabricksParameter)collection["a"]).Value);
        Assert.Equal(1, collection.Count);
    }

    [Fact]
    public void RemoveAt_by_name_removes()
    {
        var collection = new DatabricksParameterCollection();
        collection.AddWithValue("a", 1);
        collection.RemoveAt("a");
        Assert.Equal(0, collection.Count);
    }

    [Fact]
    public void Add_rejects_foreign_parameter_types()
    {
        var collection = new DatabricksParameterCollection();
        Assert.Throws<InvalidCastException>(() => collection.Add("not a parameter"));
    }
}

public class DatabricksTypeMapTests
{
    [Theory]
    [InlineData("BYTE", "TINYINT")]
    [InlineData("SHORT", "SMALLINT")]
    [InlineData("LONG", "BIGINT")]
    [InlineData("INTEGER", "INT")]
    [InlineData("int", "INT")]
    [InlineData(null, "STRING")]
    public void Normalizes_spark_type_aliases(string? input, string expected)
    {
        Assert.Equal(expected, DatabricksTypeMap.Normalize(input));
    }

    [Theory]
    [InlineData("BOOLEAN", typeof(bool))]
    [InlineData("TINYINT", typeof(sbyte))]
    [InlineData("BIGINT", typeof(long))]
    [InlineData("DOUBLE", typeof(double))]
    [InlineData("DATE", typeof(DateOnly))]
    [InlineData("TIMESTAMP", typeof(DateTime))]
    [InlineData("TIMESTAMP_NTZ", typeof(DateTime))]
    [InlineData("BINARY", typeof(byte[]))]
    [InlineData("STRING", typeof(string))]
    [InlineData("ARRAY", typeof(string))]
    [InlineData("MAP", typeof(string))]
    [InlineData("STRUCT", typeof(string))]
    [InlineData("INTERVAL", typeof(string))]
    public void Maps_databricks_types_to_net_types(string typeName, Type expected)
    {
        var column = new ColumnInfo { Name = "c", TypeName = typeName };
        Assert.Equal(expected, DatabricksTypeMap.GetFieldType(column));
    }

    [Fact]
    public void Decimal_type_depends_on_precision()
    {
        Assert.Equal(typeof(decimal), DatabricksTypeMap.GetFieldType(
            new ColumnInfo { TypeName = "DECIMAL", TypePrecision = 28 }));
        Assert.Equal(typeof(System.Data.SqlTypes.SqlDecimal), DatabricksTypeMap.GetFieldType(
            new ColumnInfo { TypeName = "DECIMAL", TypePrecision = 29 }));
    }

    [Fact]
    public void Json_binary_is_decoded_from_base64()
    {
        var column = new ColumnInfo { Name = "b", TypeName = "BINARY" };
        var value = DatabricksTypeMap.ConvertJsonValue(
            System.Convert.ToBase64String([1, 2, 3]), column);
        Assert.Equal(new byte[] { 1, 2, 3 }, value);
    }

    [Fact]
    public void Json_complex_types_pass_through_as_strings()
    {
        var column = new ColumnInfo { Name = "a", TypeName = "ARRAY", TypeText = "ARRAY<INT>" };
        Assert.Equal("[1,2,3]", DatabricksTypeMap.ConvertJsonValue("[1,2,3]", column));
    }
}
