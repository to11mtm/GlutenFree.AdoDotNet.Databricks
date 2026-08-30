using Apache.Arrow;
using Apache.Arrow.Ipc;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>
/// Covers the streaming-transport seam: a <see cref="ResultData.ArrowStream"/> payload
/// (as produced by the Thrift add-on transport) drained by the reader.
/// </summary>
public class ArrowStreamResultTests
{
    private sealed class FakeArrowStream(Schema schema, params RecordBatch[] batches) : IArrowArrayStream
    {
        private int _index;

        public bool Disposed { get; private set; }

        public Schema Schema { get; } = schema;

        public ValueTask<RecordBatch> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
            => new(_index < batches.Length ? batches[_index++] : null!);

        public void Dispose() => Disposed = true;
    }

    private static Schema IntSchema()
        => new Schema.Builder().Field(f => f.Name("v").DataType(new Apache.Arrow.Types.Int32Type()).Nullable(true)).Build();

    private static RecordBatch IntBatch(Schema schema, params int[] values)
        => new(schema, [new Int32Array.Builder().AppendRange(values).Build()], values.Length);

    private static StatementResponse StreamingResponse(Schema schema, IArrowArrayStream stream, long totalRows)
        => new()
        {
            StatementId = "stmt-arrow-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "ARROW_STREAM",
                TotalChunkCount = 1,
                TotalRowCount = totalRows,
                Schema = new ResultSchema
                {
                    ColumnCount = 1,
                    Columns = [new ColumnInfo { Name = "v", TypeName = "INT", Position = 0 }],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = totalRows, ArrowStream = stream },
        };

    [Fact]
    public async Task Reads_all_batches_from_arrow_stream_async()
    {
        var schema = IntSchema();
        var stream = new FakeArrowStream(schema, IntBatch(schema, 1, 2), IntBatch(schema, 3));
        var reader = new DatabricksDataReader(new FakeTransport(), StreamingResponse(schema, stream, 3));

        var values = new List<int>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        Assert.Equal([1, 2, 3], values);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void Reads_all_batches_from_arrow_stream_sync()
    {
        var schema = IntSchema();
        var stream = new FakeArrowStream(schema, IntBatch(schema, 10), IntBatch(schema, 20, 30));
        var reader = new DatabricksDataReader(new FakeTransport(), StreamingResponse(schema, stream, 3));

        var values = new List<int>();
        while (reader.Read())
        {
            values.Add(reader.GetInt32(0));
        }

        Assert.Equal([10, 20, 30], values);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Empty_arrow_stream_yields_no_rows_and_is_disposed()
    {
        var schema = IntSchema();
        var stream = new FakeArrowStream(schema);
        var reader = new DatabricksDataReader(new FakeTransport(), StreamingResponse(schema, stream, 0));

        Assert.False(await reader.ReadAsync());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void Closing_reader_midway_disposes_the_stream()
    {
        var schema = IntSchema();
        var stream = new FakeArrowStream(schema, IntBatch(schema, 1, 2), IntBatch(schema, 3));
        var reader = new DatabricksDataReader(new FakeTransport(), StreamingResponse(schema, stream, 3));

        Assert.True(reader.Read());
        reader.Close();

        Assert.True(stream.Disposed);
    }

    [Fact]
    public void Closing_reader_before_first_read_disposes_the_pending_stream()
    {
        var schema = IntSchema();
        var stream = new FakeArrowStream(schema, IntBatch(schema, 1));
        var reader = new DatabricksDataReader(new FakeTransport(), StreamingResponse(schema, stream, 1));

        reader.Close();

        Assert.True(stream.Disposed);
    }
}
