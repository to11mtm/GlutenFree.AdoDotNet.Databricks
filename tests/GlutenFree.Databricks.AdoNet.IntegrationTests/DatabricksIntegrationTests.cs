using System.Data;
using System.Data.SqlTypes;
using Dapper;
using GlutenFree.Databricks.AdoNet;

namespace GlutenFree.Databricks.AdoNet.IntegrationTests;

/// <summary>
/// End-to-end tests against a live Databricks SQL warehouse. Skipped unless the
/// DATABRICKS_* environment variables are set (see planning/integration-test-setup.md).
/// Uses a fixed versioned schema; rows are scoped by a per-run <c>run_id</c> and deleted
/// on cleanup (tables are never dropped, keeping the metastore table count constant).
/// </summary>
[Trait("Category", "Integration")]
public class DatabricksIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private const string Schema = "adodotnet_it_v1";
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private DatabricksConnection _connection = null!;

    public async ValueTask InitializeAsync()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            return;
        }

        _connection = IntegrationConfig.CreateConnection();
        await _connection.OpenAsync();
        await IntegrationConfig.SweepStaleSchemasAsync(_connection);
        await IntegrationConfig.EnsureVersionedSchemaAsync(
            _connection,
            Schema,
            $"CREATE TABLE IF NOT EXISTS {Catalog}.{Schema}.orders " +
            "(run_id STRING, id BIGINT, customer STRING, amount DECIMAL(10,2))",
            $"CREATE TABLE IF NOT EXISTS {Catalog}.{Schema}.schema_probe (a INT, b STRING)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await IntegrationConfig.DeleteRunRowsAsync(_connection, Schema, _runId, "orders");
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private async Task<int> ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    [IntegrationFact]
    public async Task Select_one_roundtrips_via_arrow()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 AS one";

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("one", reader.GetName(0));
        Assert.False(await reader.ReadAsync());
    }

    [IntegrationFact]
    public async Task Typed_literals_map_to_expected_net_types()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
              CAST(1 AS TINYINT)                       AS c_tinyint,
              CAST(2 AS SMALLINT)                      AS c_smallint,
              CAST(3 AS INT)                           AS c_int,
              CAST(4 AS BIGINT)                        AS c_bigint,
              CAST(1.5 AS FLOAT)                       AS c_float,
              CAST(2.5 AS DOUBLE)                      AS c_double,
              CAST('12345.67' AS DECIMAL(10,2))        AS c_decimal,
              CAST('99999999999999999999999999999999.999999' AS DECIMAL(38,6)) AS c_bigdecimal,
              'text'                                   AS c_string,
              true                                     AS c_bool,
              DATE'2026-08-29'                         AS c_date,
              TIMESTAMP'2026-08-29 12:34:56.789'       AS c_timestamp,
              CAST(NULL AS STRING)                     AS c_null,
              array(1, 2, 3)                           AS c_array,
              named_struct('a', 1, 'b', 'x')           AS c_struct
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal((sbyte)1, reader.GetValue(0));
        Assert.Equal((short)2, reader.GetValue(1));
        Assert.Equal(3, reader.GetInt32(2));
        Assert.Equal(4L, reader.GetInt64(3));
        Assert.Equal(1.5f, reader.GetFloat(4));
        Assert.Equal(2.5, reader.GetDouble(5));
        Assert.Equal(12345.67m, reader.GetDecimal(6));
        Assert.Equal(SqlDecimal.Parse("99999999999999999999999999999999.999999"), reader.GetSqlDecimal(7));
        Assert.Equal("text", reader.GetString(8));
        Assert.True(reader.GetBoolean(9));
        Assert.Equal(new DateOnly(2026, 8, 29), reader.GetDateOnly(10));
        Assert.Equal(new DateTime(2026, 8, 29, 12, 34, 56, 789, DateTimeKind.Utc), reader.GetDateTime(11));
        Assert.True(reader.IsDBNull(12));
        Assert.False(reader.IsDBNull(13)); // complex types arrive as JSON strings
        Assert.Contains("1", reader.GetString(13));
        Assert.Contains("\"a\"", reader.GetString(14));
    }

    [IntegrationFact]
    public async Task Parameters_bind_server_side()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT :num AS n, :text AS t, :day AS d";
        command.Parameters.AddWithValue("num", 42L);
        command.Parameters.AddWithValue("text", "it's safe; -- no injection");
        command.Parameters.AddWithValue("day", new DateOnly(2026, 8, 29));

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal("it's safe; -- no injection", reader.GetString(1));
        Assert.Equal(new DateOnly(2026, 8, 29), reader.GetDateOnly(2));
    }

    [IntegrationFact]
    public async Task Json_result_format_works()
    {
        await using var connection = IntegrationConfig.CreateConnection("ResultFormat=Json");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 7 AS v, 'x' AS s";

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt32(0));
        Assert.Equal("x", reader.GetString(1));
    }

    [IntegrationFact]
    public async Task Large_result_streams_multiple_batches()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, id * 2 AS doubled FROM range(100000)";

        await using var reader = await command.ExecuteReaderAsync();

        long count = 0, sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt64(1);
            count++;
        }

        Assert.Equal(100_000, count);
        Assert.Equal(9_999_900_000L, sum); // 2 * (0 + 1 + ... + 99999)
    }

    [IntegrationFact]
    public async Task Dml_and_query_lifecycle()
    {
        var table = $"{Catalog}.{Schema}.orders";

        await using (var insert = _connection.CreateCommand())
        {
            insert.CommandText =
                $"INSERT INTO {table} VALUES (:r, 1, 'alice', 12.34), (:r, 2, NULL, 56.78)";
            insert.Parameters.AddWithValue("r", _runId);
            Assert.Equal(2, await insert.ExecuteNonQueryAsync());
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT id, customer, amount FROM {table} WHERE run_id = :r ORDER BY id";
        command.Parameters.AddWithValue("r", _runId);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("alice", reader.GetString(1));
        Assert.Equal(12.34m, reader.GetDecimal(2));
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());
    }

    [IntegrationFact]
    public async Task Dapper_query_works_end_to_end()
    {
        var rows = (await _connection.QueryAsync<(long Id, string Name)>(
            "SELECT id, concat('user_', id) AS name FROM range(3) ORDER BY id")).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal((0L, "user_0"), rows[0]);
        Assert.Equal((2L, "user_2"), rows[2]);
    }

    [IntegrationFact]
    public void GetSchema_lists_fixture_table()
    {
        var tables = _connection.GetSchema("Tables", [Catalog, Schema, null]);
        Assert.Contains(tables.Rows.Cast<DataRow>(), r => (string)r["TABLE_NAME"] == "schema_probe");

        var columns = _connection.GetSchema("Columns", [Catalog, Schema, "schema_probe", null]);
        Assert.Equal(2, columns.Rows.Count);
        Assert.Equal("a", columns.Rows[0]["COLUMN_NAME"]);
    }

    [IntegrationFact]
    public async Task Failed_statement_surfaces_databricks_error()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM this_table_does_not_exist_xyz";

        var ex = await Assert.ThrowsAsync<DatabricksException>(
            async () => await command.ExecuteReaderAsync());

        Assert.Contains("TABLE_OR_VIEW_NOT_FOUND", ex.Message);
        Assert.NotNull(ex.StatementId);
    }
}
