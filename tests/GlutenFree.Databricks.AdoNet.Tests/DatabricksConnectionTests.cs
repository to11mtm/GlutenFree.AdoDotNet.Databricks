using System.Data;
using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

public class DatabricksConnectionTests
{
    internal static (DatabricksConnection Connection, FakeTransport Transport) CreateOpenable(
        string? extraConnectionString = null)
    {
        var transport = new FakeTransport();
        var connection = new DatabricksConnection(
            "Host=https://adb-1.azuredatabricks.net;WarehouseId=wh1;Token=dapi123" +
            (extraConnectionString is null ? "" : ";" + extraConnectionString))
        {
            TransportFactory = _ => transport,
        };
        return (connection, transport);
    }

    [Fact]
    public async Task Open_transitions_state_and_close_disposes_transport()
    {
        var (connection, transport) = CreateOpenable();
        Assert.Equal(ConnectionState.Closed, connection.State);

        await connection.OpenAsync();
        Assert.Equal(ConnectionState.Open, connection.State);

        await connection.CloseAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Open_validates_connection_string()
    {
        var connection = new DatabricksConnection("Host=https://x.databricks.net");
        await Assert.ThrowsAsync<ArgumentException>(() => connection.OpenAsync());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Open_twice_throws()
    {
        var (connection, _) = CreateOpenable();
        await connection.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenAsync());
    }

    [Fact]
    public async Task BeginTransaction_throws_NotSupported()
    {
        var (connection, _) = CreateOpenable();
        await connection.OpenAsync();
        Assert.Throws<NotSupportedException>(() => connection.BeginTransaction());
    }

    [Fact]
    public async Task ChangeDatabase_and_ChangeCatalog_update_statement_namespace()
    {
        var (connection, transport) = CreateOpenable("Catalog=main;Schema=default");
        await connection.OpenAsync();
        connection.ChangeDatabase("analytics");
        connection.ChangeCatalog("hive_metastore");

        transport.NextResponse = Responses.EmptySuccess;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteReaderAsync();

        var request = Assert.Single(transport.ExecutedRequests);
        Assert.Equal("hive_metastore", request.Catalog);
        Assert.Equal("analytics", request.Schema);
    }

    [Fact]
    public void ConnectionString_cannot_change_while_open()
    {
        var (connection, _) = CreateOpenable();
        connection.Open();
        Assert.Throws<InvalidOperationException>(
            () => connection.ConnectionString = "Host=https://other;WarehouseId=w;Token=t");
    }
}

internal static class Responses
{
    internal static StatementResponse EmptySuccess => new()
    {
        StatementId = "stmt-1",
        Status = new StatementStatus { State = "SUCCEEDED" },
        Manifest = new ResultManifest
        {
            Format = "JSON_ARRAY",
            TotalChunkCount = 0,
            TotalRowCount = 0,
            Schema = new ResultSchema { ColumnCount = 0, Columns = [] },
        },
    };
}
