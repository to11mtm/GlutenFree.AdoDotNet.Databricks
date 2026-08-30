using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.Databricks.AdoNet.Linq2Db;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.IntegrationTests;

/// <summary>
/// Live coverage of extended Databricks types through the linq2db provider:
/// TIMESTAMP_NTZ, INTERVAL, ARRAY/MAP/STRUCT (as JSON strings), VARIANT, and CHAR/VARCHAR.
/// </summary>
[Trait("Category", "Integration")]
public class Linq2DbExtendedTypesIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private const string Schema = "adodotnet_l2ext_v1";
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private DatabricksConnection _connection = null!;

    private sealed class ExtRow
    {
        [Column("run_id"), PrimaryKey(0)] public string RunId { get; set; } = "";

        [Column("id"), PrimaryKey(1)] public long Id { get; set; }

        [Column("ntz")] public DateTime? Ntz { get; set; }

        [Column("ch")] public string? Ch { get; set; }

        [Column("vc")] public string? Vc { get; set; }

        // Complex types surface as JSON strings in v1.
        [Column("arr")] public string? Arr { get; set; }

        [Column("mp")] public string? Mp { get; set; }

        [Column("st")] public string? St { get; set; }

        [Column("vr")] public string? Vr { get; set; }
    }

    private ITable<ExtRow> RowsTable(DataConnection db)
        => db.GetTable<ExtRow>().TableName("t_ext").SchemaName(Schema).ServerName(Catalog);

    private IQueryable<ExtRow> Rows(DataConnection db)
        => RowsTable(db).Where(r => r.RunId == _runId);

    public async Task InitializeAsync()
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
            $"CREATE TABLE IF NOT EXISTS {Catalog}.{Schema}.t_ext (" +
            "run_id STRING, id BIGINT, ntz TIMESTAMP_NTZ, ch CHAR(5), vc VARCHAR(10), " +
            "arr ARRAY<INT>, mp MAP<STRING, INT>, st STRUCT<a: INT, b: STRING>, vr VARIANT)");
        await ExecuteAsync(
            $"INSERT INTO {Catalog}.{Schema}.t_ext VALUES (" +
            $"'{_runId}', 1, TIMESTAMP_NTZ'2026-08-29 12:34:56.789', 'abcde', 'hello', " +
            "array(1, 2, 3), map('a', 1, 'b', 2), named_struct('a', 42, 'b', 'x'), " +
            """parse_json('{"name": "alice", "tags": [1, 2]}'))""");
    }

    public async Task DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await IntegrationConfig.DeleteRunRowsAsync(_connection, Schema, _runId, "t_ext");
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [IntegrationFact]
    public void Entity_with_extended_types_materializes()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var row = Rows(db).Single(r => r.Id == 1L);

        Assert.Equal(new DateTime(2026, 8, 29, 12, 34, 56, 789), row.Ntz);
        Assert.Equal("abcde", row.Ch);
        Assert.Equal("hello", row.Vc);
        Assert.Contains("1", row.Arr);
        Assert.Contains("\"a\"", row.Mp);
        Assert.Contains("42", row.St);
        Assert.Contains("alice", row.Vr);
    }

    [IntegrationFact]
    public void Insert_and_filter_timestamp_ntz_and_char_types()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);
        var ntz = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        RowsTable(db).Insert(() => new ExtRow
        {
            RunId = _runId,
            Id = 2,
            Ntz = ntz,
            Ch = "zzzzz",
            Vc = "world",
        });

        // Filter on the TIMESTAMP_NTZ column via a parameter.
        var stored = Rows(db).Single(r => r.Ntz == ntz);
        Assert.Equal(2L, stored.Id);
        Assert.Equal("zzzzz", stored.Ch);
        Assert.Equal("world", stored.Vc);
        Assert.Null(stored.Arr);
        Assert.True(stored.Vr is null);
    }

    [IntegrationFact]
    public void Interval_expressions_read_as_strings()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var row = db.SelectQuery(() => new
        {
            YearMonth = Sql.Expr<string>("INTERVAL '2-3' YEAR TO MONTH"),
            DayTime = Sql.Expr<string>("INTERVAL '5 04:03:02.1' DAY TO SECOND"),
        }).Single();

        Assert.Contains("2-3", row.YearMonth);
        Assert.False(string.IsNullOrEmpty(row.DayTime));
    }

    [IntegrationFact]
    public void Complex_type_json_can_be_queried_with_sql_functions()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        // Server-side element access on complex columns, materialized to simple types.
        var projected = Rows(db)
            .Where(r => r.Id == 1L)
            .Select(r => new
            {
                First = Sql.Expr<int>("arr[0]"),
                MapA = Sql.Expr<int>("mp['a']"),
                StructB = Sql.Expr<string>("st.b"),
            })
            .Single();

        Assert.Equal(1, projected.First);
        Assert.Equal(1, projected.MapA);
        Assert.Equal("x", projected.StructB);
    }

    [IntegrationFact]
    public void Group_by_on_timestamp_ntz_date_works()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var counts = Rows(db)
            .Where(r => r.Ntz != null)
            .GroupBy(r => r.Ntz!.Value.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToList();

        Assert.NotEmpty(counts);
        Assert.Contains(counts, c => c.Day == new DateTime(2026, 8, 29));
    }
}
