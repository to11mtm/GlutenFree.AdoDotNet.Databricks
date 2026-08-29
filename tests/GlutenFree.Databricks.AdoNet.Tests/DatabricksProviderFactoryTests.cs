using System.Data.Common;
using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

public class DatabricksProviderFactoryTests
{
    [Fact]
    public void Factory_creates_provider_types()
    {
        var factory = DatabricksProviderFactory.Instance;

        Assert.IsType<DatabricksConnection>(factory.CreateConnection());
        Assert.IsType<DatabricksCommand>(factory.CreateCommand());
        Assert.IsType<DatabricksParameter>(factory.CreateParameter());
        Assert.IsType<DatabricksConnectionStringBuilder>(factory.CreateConnectionStringBuilder());
        Assert.False(factory.CanCreateDataAdapter);
        Assert.False(factory.CanCreateCommandBuilder);
    }

    [Fact]
    public void Factory_can_be_registered_and_resolved()
    {
        DbProviderFactories.RegisterFactory("GlutenFree.Databricks.AdoNet.TestReg", DatabricksProviderFactory.Instance);

        var resolved = DbProviderFactories.GetFactory("GlutenFree.Databricks.AdoNet.TestReg");

        Assert.Same(DatabricksProviderFactory.Instance, resolved);
    }
}

public class GetSchemaTests
{
    private static (DatabricksConnection Connection, FakeTransport Transport) CreateOpen()
    {
        var (connection, transport) = DatabricksConnectionTests.CreateOpenable();
        connection.Open();
        return (connection, transport);
    }

    private static StatementResponse TablesResponse => new()
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
                    new ColumnInfo { Name = "TABLE_CATALOG", TypeName = "STRING", Position = 0 },
                    new ColumnInfo { Name = "TABLE_SCHEMA", TypeName = "STRING", Position = 1 },
                    new ColumnInfo { Name = "TABLE_NAME", TypeName = "STRING", Position = 2 },
                    new ColumnInfo { Name = "TABLE_TYPE", TypeName = "STRING", Position = 3 },
                ],
            },
        },
        Result = new ResultData
        {
            ChunkIndex = 0,
            RowCount = 2,
            DataArray =
            [
                ["main", "default", "orders", "MANAGED"],
                ["main", "default", "users", "MANAGED"],
            ],
        },
    };

    [Fact]
    public void MetaDataCollections_lists_supported_collections_without_opening()
    {
        var connection = new DatabricksConnection();

        var collections = connection.GetSchema();

        var names = collections.Rows.Cast<System.Data.DataRow>()
            .Select(r => (string)r["CollectionName"]).ToArray();
        Assert.Contains("Tables", names);
        Assert.Contains("Columns", names);
        Assert.Contains("Catalogs", names);
        Assert.Contains("Schemas", names);
        Assert.Contains("Views", names);
    }

    [Fact]
    public void Tables_collection_queries_information_schema()
    {
        var (connection, transport) = CreateOpen();
        transport.NextResponse = TablesResponse;

        var tables = connection.GetSchema("Tables");

        Assert.Equal(2, tables.Rows.Count);
        Assert.Equal("orders", tables.Rows[0]["TABLE_NAME"]);
        var request = Assert.Single(transport.ExecutedRequests);
        Assert.Contains("system.information_schema.tables", request.Statement);
        Assert.Null(request.Parameters);
    }

    [Fact]
    public void Restrictions_are_bound_as_parameters_not_interpolated()
    {
        var (connection, transport) = CreateOpen();
        transport.NextResponse = TablesResponse;

        connection.GetSchema("Tables", ["main", "default'; DROP TABLE x --", null]);

        var request = Assert.Single(transport.ExecutedRequests);
        Assert.Contains("table_catalog = :r_table_catalog", request.Statement);
        Assert.Contains("table_schema = :r_table_schema", request.Statement);
        Assert.DoesNotContain("DROP TABLE", request.Statement);
        Assert.Equal(2, request.Parameters!.Count);
        Assert.Equal("default'; DROP TABLE x --", request.Parameters[1].Value);
    }

    [Fact]
    public void Unknown_collection_throws()
    {
        var (connection, _) = CreateOpen();
        Assert.Throws<ArgumentException>(() => connection.GetSchema("IndexColumns"));
    }

    [Fact]
    public void Data_collections_require_open_connection()
    {
        var connection = new DatabricksConnection(
            "Host=https://adb-1.azuredatabricks.net;WarehouseId=wh1;Token=t");

        Assert.Throws<InvalidOperationException>(() => connection.GetSchema("Tables"));
    }
}
