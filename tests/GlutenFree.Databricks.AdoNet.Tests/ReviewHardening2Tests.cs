using System.Data;
using System.Reflection;
using Apache.Arrow;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>
/// Tests for the second round of PR-review fixes: ConnectionTimeout metadata, reader
/// Close() resource release, explicit DbType coercion, DatabricksDecimal precision,
/// DECIMAL(39+) parameter rejection, and nested Arrow value fidelity.
/// </summary>
public class ReviewHardening2Tests
{
    [Fact]
    public void ConnectionTimeout_reflects_connect_timeout_setting()
    {
        var connection = new DatabricksConnection(
            "Host=https://adb-1.azuredatabricks.net;WarehouseId=w;Token=t;ConnectTimeout=77");

        Assert.Equal(77, connection.ConnectionTimeout);
    }

    [Fact]
    public async Task Reader_Close_releases_arrow_resources()
    {
        var schema = new Schema.Builder()
            .Field(f => f.Name("id").DataType(Apache.Arrow.Types.Int32Type.Default))
            .Build();
        var batch = new RecordBatch(schema, [new Int32Array.Builder().Append(1).Build()], 1);
        using var stream = new MemoryStream();
        using (var writer = new Apache.Arrow.Ipc.ArrowStreamWriter(stream, schema))
        {
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }

        var reader = new DatabricksDataReader(new FakeTransport(), new StatementResponse
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "ARROW_STREAM",
                TotalChunkCount = 1,
                TotalRowCount = 1,
                Schema = new ResultSchema
                {
                    ColumnCount = 1,
                    Columns = [new ColumnInfo { Name = "id", TypeName = "INT", Position = 0 }],
                },
            },
            Result = new ResultData
            {
                ChunkIndex = 0,
                RowCount = 1,
                Attachment = Convert.ToBase64String(stream.ToArray()),
            },
        });
        Assert.True(await reader.ReadAsync());

        reader.Close();

        var type = typeof(DatabricksDataReader);
        Assert.Null(type.GetField("_arrowBatch", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader));
        Assert.Null(type.GetField("_arrowReader", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader));
    }

    [Theory]
    [InlineData(42, DbType.Int64, "42", "BIGINT")]
    [InlineData(42L, DbType.Int32, "42", "INT")]
    [InlineData((short)7, DbType.SByte, "7", "TINYINT")]
    [InlineData(1.5, DbType.Single, "1.5", "FLOAT")]
    [InlineData(123, DbType.String, "123", "STRING")]
    [InlineData("250", DbType.Int32, "250", "INT")]
    public void Explicit_DbType_overrides_runtime_type_inference(
        object value, DbType dbType, string expectedValue, string expectedType)
    {
        var parameter = new DatabricksParameter("p", value) { DbType = dbType };

        var wire = parameter.ToStatementParameter();

        Assert.Equal(expectedValue, wire.Value);
        Assert.Equal(expectedType, wire.Type);
    }

    [Fact]
    public void Explicit_Date_DbType_converts_datetime_to_date()
    {
        var parameter = new DatabricksParameter("p", new DateTime(2026, 8, 29, 13, 0, 0))
        {
            DbType = DbType.Date,
        };

        var wire = parameter.ToStatementParameter();

        Assert.Equal("2026-08-29", wire.Value);
        Assert.Equal("DATE", wire.Type);
    }

    [Fact]
    public void Explicit_Decimal_DbType_converts_double()
    {
        var parameter = new DatabricksParameter("p", 2.5) { DbType = DbType.Decimal };

        var wire = parameter.ToStatementParameter();

        Assert.Equal("2.5", wire.Value);
        Assert.StartsWith("DECIMAL(", wire.Type);
    }

    [Theory]
    [InlineData("9", 1)]
    [InlineData("10", 2)]
    [InlineData("100", 3)]
    [InlineData("1000000000000000000", 19)] // 10^18: Log10 rounding hazard zone
    [InlineData("999999999999999999", 18)]
    [InlineData("100000000000000000000000000000000000000", 39)] // 10^38
    [InlineData("99999999999999999999999999999999999999", 38)]
    public void DatabricksDecimal_precision_is_exact_near_powers_of_ten(string value, int expected)
    {
        Assert.Equal(expected, DatabricksDecimal.Parse(value).Precision);
    }

    [Fact]
    public void Parameter_rejects_DatabricksDecimal_beyond_38_digits()
    {
        var tooWide = DatabricksDecimal.Parse("123456789012345678901234567890123456789"); // 39 digits
        var parameter = new DatabricksParameter("p", tooWide);

        var ex = Assert.Throws<NotSupportedException>(parameter.ToStatementParameter);
        Assert.Contains("38", ex.Message);
    }

    [Fact]
    public void Nested_interval_in_array_serializes_as_interval_string()
    {
        var listBuilder = new ListArray.Builder(Apache.Arrow.Types.IntervalType.YearMonth);
        var valueBuilder = (YearMonthIntervalArray.Builder)listBuilder.ValueBuilder;
        listBuilder.Append();
        valueBuilder.Append(new Apache.Arrow.Scalars.YearMonthInterval(27)); // 2 years 3 months
        var list = listBuilder.Build();

        var json = (string)DatabricksTypeMap.ConvertArrowValue(
            list, 0, new ColumnInfo { Name = "a", TypeName = "ARRAY" });

        Assert.Equal("""["2-3"]""", json);
    }

    [Fact]
    public void Nested_unsupported_array_type_throws_instead_of_fabricating_json()
    {
        var listBuilder = new ListArray.Builder(Apache.Arrow.Types.Time32Type.Default);
        var valueBuilder = (Time32Array.Builder)listBuilder.ValueBuilder;
        listBuilder.Append();
        valueBuilder.Append(1234);
        var list = listBuilder.Build();

        Assert.Throws<NotSupportedException>(() => DatabricksTypeMap.ConvertArrowValue(
            list, 0, new ColumnInfo { Name = "a", TypeName = "ARRAY" }));
    }
}
