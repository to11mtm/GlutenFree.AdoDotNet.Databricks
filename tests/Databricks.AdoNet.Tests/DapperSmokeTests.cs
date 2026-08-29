using Dapper;
using Databricks.AdoNet.Transport;

namespace Databricks.AdoNet.Tests;

/// <summary>
/// Verifies the provider works with Dapper out of the box (DbConnection/DbCommand/DbParameter
/// contract compliance), using the in-memory transport.
/// </summary>
public class DapperSmokeTests
{
    private sealed record Order(long Id, string? Customer, decimal Amount, DateTime CreatedAt);

    private static StatementResponse OrdersResponse => new()
    {
        StatementId = "stmt-1",
        Status = new StatementStatus { State = "SUCCEEDED" },
        Manifest = new ResultManifest
        {
            Format = "JSON_ARRAY",
            TotalChunkCount = 1,
            TotalRowCount = 2,
            Schema = new ResultSchema
            {
                ColumnCount = 4,
                Columns =
                [
                    new ColumnInfo { Name = "Id", TypeName = "BIGINT", Position = 0 },
                    new ColumnInfo { Name = "Customer", TypeName = "STRING", Position = 1 },
                    new ColumnInfo { Name = "Amount", TypeName = "DECIMAL", TypePrecision = 10, TypeScale = 2, Position = 2 },
                    new ColumnInfo { Name = "CreatedAt", TypeName = "TIMESTAMP", Position = 3 },
                ],
            },
        },
        Result = new ResultData
        {
            ChunkIndex = 0,
            RowCount = 2,
            DataArray =
            [
                ["1", "alice", "12.34", "2026-08-29T12:00:00Z"],
                ["2", null, "56.78", "2026-01-01T00:00:00Z"],
            ],
        },
    };

    [Fact]
    public void Dapper_Query_maps_rows_to_poco()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        connection.Open();
        transport.NextResponse = OrdersResponse;

        var orders = connection.Query<Order>("SELECT * FROM orders").ToList();

        Assert.Equal(2, orders.Count);
        Assert.Equal(new Order(1, "alice", 12.34m, new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc)), orders[0]);
        Assert.Equal(2, orders[1].Id);
        Assert.Null(orders[1].Customer);
    }

    [Fact]
    public async Task Dapper_QueryAsync_with_parameters_binds_natively()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        await connection.OpenAsync();
        transport.NextResponse = OrdersResponse;

        var orders = (await connection.QueryAsync<Order>(
            "SELECT * FROM orders WHERE amount > :minAmount AND customer = :customer",
            new { minAmount = 10.5, customer = "alice" })).ToList();

        Assert.Equal(2, orders.Count);
        var request = Assert.Single(transport.ExecutedRequests);
        Assert.Equal(2, request.Parameters!.Count);
        var min = request.Parameters.Single(p => p.Name == "minAmount");
        Assert.Equal(("10.5", "DOUBLE"), (min.Value, min.Type));
        var customer = request.Parameters.Single(p => p.Name == "customer");
        Assert.Equal(("alice", "STRING"), (customer.Value, customer.Type));
    }

    [Fact]
    public async Task Dapper_ExecuteScalarAsync_works()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        await connection.OpenAsync();
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
                    Columns = [new ColumnInfo { Name = "cnt", TypeName = "BIGINT", Position = 0 }],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [["42"]] },
        };

        var count = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM orders");

        Assert.Equal(42, count);
    }
}
