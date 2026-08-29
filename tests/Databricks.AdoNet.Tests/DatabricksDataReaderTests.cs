using System.Data.SqlTypes;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Databricks.AdoNet.Transport;

namespace Databricks.AdoNet.Tests;

public class DatabricksDataReaderTests
{
    private static StatementResponse JsonResponse(
        IReadOnlyList<ColumnInfo> columns,
        ResultData? result,
        int totalChunks = 1,
        long totalRows = 1)
        => new()
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "JSON_ARRAY",
                TotalChunkCount = totalChunks,
                TotalRowCount = totalRows,
                Schema = new ResultSchema { ColumnCount = columns.Count, Columns = columns },
            },
            Result = result,
        };

    [Fact]
    public async Task Reads_and_converts_json_values()
    {
        var columns = new[]
        {
            new ColumnInfo { Name = "i", TypeName = "INT", Position = 0 },
            new ColumnInfo { Name = "s", TypeName = "STRING", Position = 1 },
            new ColumnInfo { Name = "d", TypeName = "DOUBLE", Position = 2 },
            new ColumnInfo { Name = "b", TypeName = "BOOLEAN", Position = 3 },
            new ColumnInfo { Name = "dt", TypeName = "DATE", Position = 4 },
            new ColumnInfo { Name = "dec", TypeName = "DECIMAL", TypePrecision = 10, TypeScale = 2, Position = 5 },
        };
        var reader = new DatabricksDataReader(new FakeTransport(), JsonResponse(columns, new ResultData
        {
            ChunkIndex = 0,
            RowCount = 2,
            DataArray =
            [
                ["1", "hello", "2.5", "true", "2026-08-29", "1234.56"],
                ["2", null, "3.5", "false", "2026-01-01", "0.01"],
            ],
        }, totalRows: 2));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("hello", reader.GetString(1));
        Assert.Equal(2.5, reader.GetDouble(2));
        Assert.True(reader.GetBoolean(3));
        Assert.Equal(new DateOnly(2026, 8, 29), reader.GetDateOnly(4));
        Assert.Equal(1234.56m, reader.GetDecimal(5));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(DBNull.Value, reader.GetValue(1));

        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Fetches_additional_json_chunks_from_transport()
    {
        var transport = new FakeTransport();
        transport.Chunks[1] = new ResultData
        {
            ChunkIndex = 1,
            RowCount = 1,
            DataArray = [["2"]],
        };
        var columns = new[] { new ColumnInfo { Name = "v", TypeName = "INT", Position = 0 } };
        var reader = new DatabricksDataReader(transport, JsonResponse(
            columns,
            new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [["1"]] },
            totalChunks: 2,
            totalRows: 2));

        var values = new List<int>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        Assert.Equal([1, 2], values);
        Assert.Equal([("stmt-1", 1)], transport.ChunkRequests);
    }

    [Fact]
    public async Task Reads_arrow_stream_from_external_link()
    {
        var arrowBytes = BuildArrowChunk();
        var transport = new FakeTransport();
        transport.ExternalLinkData["mem://chunk0"] = arrowBytes;

        var columns = new[]
        {
            new ColumnInfo { Name = "id", TypeName = "INT", Position = 0 },
            new ColumnInfo { Name = "name", TypeName = "STRING", Position = 1 },
            new ColumnInfo { Name = "amount", TypeName = "DECIMAL", TypePrecision = 38, TypeScale = 2, Position = 2 },
            new ColumnInfo { Name = "when", TypeName = "TIMESTAMP", Position = 3 },
            new ColumnInfo { Name = "day", TypeName = "DATE", Position = 4 },
        };
        var reader = new DatabricksDataReader(transport, new StatementResponse
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "ARROW_STREAM",
                TotalChunkCount = 1,
                TotalRowCount = 2,
                Schema = new ResultSchema { ColumnCount = columns.Length, Columns = columns },
            },
            Result = new ResultData
            {
                ChunkIndex = 0,
                RowCount = 2,
                ExternalLinks = [new ExternalLink { ChunkIndex = 0, Link = "mem://chunk0", RowCount = 2 }],
            },
        });

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("alpha", reader.GetString(1));
        // Precision 38 > 28: exposed as SqlDecimal.
        Assert.Equal(typeof(SqlDecimal), reader.GetFieldType(2));
        Assert.Equal(SqlDecimal.Parse("12345.67"), reader.GetSqlDecimal(2));
        Assert.Equal(12345.67m, reader.GetDecimal(2));
        Assert.Equal(new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc), reader.GetDateTime(3));
        Assert.Equal(new DateOnly(2026, 8, 29), reader.GetDateOnly(4));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1));

        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task GetSchemaTable_reflects_manifest()
    {
        var columns = new[]
        {
            new ColumnInfo { Name = "a", TypeName = "BIGINT", TypeText = "BIGINT", Position = 0 },
            new ColumnInfo { Name = "b", TypeName = "DECIMAL", TypeText = "DECIMAL(38,2)", TypePrecision = 38, TypeScale = 2, Position = 1 },
        };
        var reader = new DatabricksDataReader(new FakeTransport(), JsonResponse(columns, null, 0, 0));

        var schema = reader.GetSchemaTable();

        Assert.Equal(2, schema.Rows.Count);
        Assert.Equal("a", schema.Rows[0]["ColumnName"]);
        Assert.Equal(typeof(long), schema.Rows[0]["DataType"]);
        Assert.Equal("DECIMAL(38,2)", schema.Rows[1]["DataTypeName"]);
        Assert.Equal(typeof(SqlDecimal), schema.Rows[1]["DataType"]);
        Assert.Equal(38, schema.Rows[1]["NumericPrecision"]);
        Assert.Equal(2, schema.Rows[1]["NumericScale"]);
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task High_precision_decimal_json_is_lossless_via_SqlDecimal()
    {
        const string bigValue = "99999999999999999999999999999999.999999";
        var columns = new[]
        {
            new ColumnInfo { Name = "d", TypeName = "DECIMAL", TypePrecision = 38, TypeScale = 6, Position = 0 },
        };
        var reader = new DatabricksDataReader(new FakeTransport(), JsonResponse(columns, new ResultData
        {
            ChunkIndex = 0,
            RowCount = 1,
            DataArray = [[bigValue]],
        }));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(SqlDecimal.Parse(bigValue), reader.GetSqlDecimal(0));
        Assert.Equal(SqlDecimal.Parse(bigValue), reader.GetFieldValue<SqlDecimal>(0));
        Assert.Throws<OverflowException>(() => reader.GetDecimal(0));
    }

    [Fact]
    public void GetValue_before_Read_throws()
    {
        var columns = new[] { new ColumnInfo { Name = "v", TypeName = "INT", Position = 0 } };
        var reader = new DatabricksDataReader(new FakeTransport(), JsonResponse(columns, null, 0, 0));

        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
    }

    private static byte[] BuildArrowChunk()
    {
        var schema = new Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default))
            .Field(f => f.Name("name").DataType(StringType.Default).Nullable(true))
            .Field(f => f.Name("amount").DataType(new Decimal128Type(38, 2)))
            .Field(f => f.Name("when").DataType(new TimestampType(TimeUnit.Microsecond, "UTC")))
            .Field(f => f.Name("day").DataType(Date32Type.Default))
            .Build();

        var ids = new Int32Array.Builder().Append(1).Append(2).Build();
        var names = new StringArray.Builder().Append("alpha").AppendNull().Build();
        var amounts = new Decimal128Array.Builder(new Decimal128Type(38, 2))
            .Append(12345.67m).Append(1.00m).Build();
        var timestamps = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC"))
            .Append(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero))
            .Append(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .Build();
        var days = new Date32Array.Builder()
            .Append(new DateOnly(2026, 8, 29)).Append(new DateOnly(2026, 1, 1)).Build();

        var batch = new RecordBatch(schema, [ids, names, amounts, timestamps, days], 2);

        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema))
        {
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }

        return stream.ToArray();
    }
}
