using Databricks.AdoNet.Linq2Db;
using Databricks.AdoNet.Tests;
using Databricks.AdoNet.Transport;
using LinqToDB;
using LinqToDB.Mapping;

namespace Databricks.AdoNet.Linq2Db.Tests;

/// <summary>
/// Verifies the linq2db provider generates valid Databricks SQL, using the in-memory transport.
/// </summary>
public class Linq2DbProviderTests
{
    [Table("orders")]
    private sealed class Order
    {
        [Column("id")] public long Id { get; set; }

        [Column("customer_name")] public string? CustomerName { get; set; }

        [Column("amount")] public decimal Amount { get; set; }
    }

    private static StatementResponse OrdersResponse(params string?[][] rows) => new()
    {
        StatementId = "stmt-1",
        Status = new StatementStatus { State = "SUCCEEDED" },
        Manifest = new ResultManifest
        {
            Format = "JSON_ARRAY",
            TotalChunkCount = 1,
            TotalRowCount = rows.Length,
            Schema = new ResultSchema
            {
                ColumnCount = 3,
                Columns =
                [
                    new ColumnInfo { Name = "id", TypeName = "BIGINT", Position = 0 },
                    new ColumnInfo { Name = "customer_name", TypeName = "STRING", Position = 1 },
                    new ColumnInfo { Name = "amount", TypeName = "DECIMAL", TypePrecision = 10, TypeScale = 2, Position = 2 },
                ],
            },
        },
        Result = new ResultData { ChunkIndex = 0, RowCount = rows.Length, DataArray = rows },
    };

    private static string Normalize(string sql)
        => string.Join(' ', sql.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    private static (LinqToDB.Data.DataConnection Db, FakeTransport Transport) CreateDb()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        var db = DatabricksTools.CreateDataConnection(connection);
        return (db, transport);
    }

    [Fact]
    public void Where_query_generates_backticked_sql_and_maps_rows()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = OrdersResponse(["1", "alice", "150.00"]);

        var minAmount = 100m;
        var orders = db.GetTable<Order>().Where(o => o.Amount > minAmount).ToList();

        var sql = Assert.Single(transport.ExecutedRequests).Statement;
        Assert.Contains("`orders`", sql);
        Assert.Contains("`amount`", sql);
        var order = Assert.Single(orders);
        Assert.Equal(1L, order.Id);
        Assert.Equal("alice", order.CustomerName);
        Assert.Equal(150.00m, order.Amount);
    }

    [Fact]
    public void Parameters_use_databricks_markers_and_flow_to_transport()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = OrdersResponse();

        var name = "alice";
        db.GetTable<Order>().Where(o => o.CustomerName == name).ToList();

        var request = Assert.Single(transport.ExecutedRequests);
        Assert.Contains(":", request.Statement);
        var parameter = Assert.Single(request.Parameters!);
        Assert.Equal("alice", parameter.Value);
        Assert.Contains($":{parameter.Name}", request.Statement);
    }

    [Fact]
    public void Take_and_skip_generate_limit_offset()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = OrdersResponse();

        db.GetTable<Order>().OrderBy(o => o.Id).Skip(5).Take(10).ToList();

        var sql = Assert.Single(transport.ExecutedRequests).Statement;
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
    }

    [Fact]
    public void Insert_generates_insert_statement()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = Responses.EmptySuccess;

        db.Insert(new Order { Id = 7, CustomerName = "bob", Amount = 9.99m });

        var sql = Normalize(Assert.Single(transport.ExecutedRequests).Statement);
        Assert.StartsWith("INSERT INTO `orders`", sql);
        Assert.Contains("`customer_name`", sql);
    }

    [Fact]
    public void Delete_generates_delete_statement()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = Responses.EmptySuccess;

        db.GetTable<Order>().Where(o => o.Id == 7L).Delete();

        var sql = Normalize(Assert.Single(transport.ExecutedRequests).Statement);
        // Databricks DELETE supports a table alias: DELETE FROM `orders` `o` WHERE ...
        Assert.StartsWith("DELETE FROM `orders`", sql);
        Assert.Contains("WHERE", sql);
    }

    [Fact]
    public void Update_generates_update_statement()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
        transport.NextResponse = Responses.EmptySuccess;

        db.GetTable<Order>()
            .Where(o => o.Id == 7L)
            .Set(o => o.Amount, 1.23m)
            .Update();

        var sql = Normalize(Assert.Single(transport.ExecutedRequests).Statement);
        Assert.StartsWith("UPDATE `orders`", sql);
        Assert.Contains("SET `amount` =", sql);
    }

    [Fact]
    public void BeginTransaction_is_noop_guarded_by_provider()
    {
        var (db, transport) = CreateDb();
        using var _ = db;

        // TransactionsSupported=false: linq2db skips the underlying BeginTransaction call
        // (which would throw NotSupportedException on DatabricksConnection).
        using var tx = db.BeginTransaction();

        Assert.NotNull(tx);
        Assert.Empty(transport.ExecutedRequests);
    }

    [Fact]
    public void Aggregate_count_works()
    {
        var (db, transport) = CreateDb();
        using var _ = db;
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
                    Columns = [new ColumnInfo { Name = "cnt", TypeName = "INT", Position = 0 }],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [["3"]] },
        };

        var count = db.GetTable<Order>().Count();

        Assert.Equal(3, count);
        Assert.Contains("Count", Assert.Single(transport.ExecutedRequests).Statement,
            StringComparison.OrdinalIgnoreCase);
    }
}
