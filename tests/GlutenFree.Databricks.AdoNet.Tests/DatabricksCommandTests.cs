using System.Data;
using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

public class DatabricksCommandTests
{
    private static (DatabricksCommand Command, FakeTransport Transport) CreateCommand(
        string? extra = null)
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable(extra);
        connection.Open();
        var command = connection.CreateCommand();
        return (command, transport);
    }

    [Fact]
    public async Task Execute_sends_statement_with_warehouse_and_format()
    {
        var (command, transport) = CreateCommand();
        transport.NextResponse = Responses.EmptySuccess;
        command.CommandText = "SELECT * FROM t";

        await command.ExecuteReaderAsync();

        var request = Assert.Single(transport.ExecutedRequests);
        Assert.Equal("SELECT * FROM t", request.Statement);
        Assert.Equal("wh1", request.WarehouseId);
        Assert.Equal("ARROW_STREAM", request.Format);
        Assert.Equal("EXTERNAL_LINKS", request.Disposition);
    }

    [Fact]
    public async Task Json_format_uses_inline_disposition_by_default()
    {
        var (command, transport) = CreateCommand("ResultFormat=Json");
        transport.NextResponse = Responses.EmptySuccess;
        command.CommandText = "SELECT 1";

        await command.ExecuteReaderAsync();

        Assert.Equal("JSON_ARRAY", transport.ExecutedRequests[0].Format);
        Assert.Equal("INLINE", transport.ExecutedRequests[0].Disposition);
    }

    [Fact]
    public async Task Arrow_with_inline_disposition_throws()
    {
        var (command, _) = CreateCommand("Disposition=Inline");
        command.CommandText = "SELECT 1";

        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync());
    }

    [Fact]
    public async Task Parameters_are_sent_with_inferred_types()
    {
        var (command, transport) = CreateCommand();
        transport.NextResponse = Responses.EmptySuccess;
        command.CommandText = "SELECT * FROM t WHERE id = :id AND name = :name AND ts > :ts";
        command.Parameters.AddWithValue("id", 42L);
        command.Parameters.AddWithValue("name", "abc");
        command.Parameters.AddWithValue("ts", new DateOnly(2026, 8, 29));

        await command.ExecuteReaderAsync();

        var parameters = transport.ExecutedRequests[0].Parameters!;
        Assert.Equal(3, parameters.Count);
        Assert.Equal(("id", "42", "BIGINT"), (parameters[0].Name, parameters[0].Value, parameters[0].Type));
        Assert.Equal(("name", "abc", "STRING"), (parameters[1].Name, parameters[1].Value, parameters[1].Type));
        Assert.Equal(("ts", "2026-08-29", "DATE"), (parameters[2].Name, parameters[2].Value, parameters[2].Type));
    }

    [Fact]
    public void CommandType_StoredProcedure_throws()
    {
        var (command, _) = CreateCommand();
        Assert.Throws<NotSupportedException>(() => command.CommandType = CommandType.StoredProcedure);
    }

    [Fact]
    public void Output_parameter_direction_throws()
    {
        var parameter = new DatabricksParameter("p", 1);
        Assert.Throws<NotSupportedException>(() => parameter.Direction = ParameterDirection.Output);
    }

    [Fact]
    public async Task ExecuteNonQuery_returns_num_affected_rows()
    {
        var (command, transport) = CreateCommand();
        transport.NextResponse = new StatementResponse
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "JSON_ARRAY",
                TotalChunkCount = 1,
                TotalRowCount = 1,
                Schema = new ResultSchema
                {
                    ColumnCount = 1,
                    Columns = [new ColumnInfo { Name = "num_affected_rows", TypeName = "BIGINT", Position = 0 }],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [["7"]] },
        };
        command.CommandText = "DELETE FROM t WHERE x > 1";

        Assert.Equal(7, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ExecuteNonQuery_returns_minus_one_without_affected_rows()
    {
        var (command, transport) = CreateCommand();
        transport.NextResponse = Responses.EmptySuccess;
        command.CommandText = "CREATE TABLE t (a INT)";

        Assert.Equal(-1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ExecuteScalar_returns_first_value()
    {
        var (command, transport) = CreateCommand();
        transport.NextResponse = new StatementResponse
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "JSON_ARRAY",
                TotalChunkCount = 1,
                TotalRowCount = 1,
                Schema = new ResultSchema
                {
                    ColumnCount = 1,
                    Columns = [new ColumnInfo { Name = "c", TypeName = "INT", Position = 0 }],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [["123"]] },
        };
        command.CommandText = "SELECT count(*) FROM t";

        Assert.Equal(123, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExecuteScalar_returns_null_for_empty_result()
    {
        var (command, transport) = CreateCommand();
        transport.NextResponse = Responses.EmptySuccess;
        command.CommandText = "SELECT 1 WHERE false";

        Assert.Null(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Execute_without_command_text_throws()
    {
        var (command, _) = CreateCommand();
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteReaderAsync());
    }
}
