using GlutenFree.Databricks.AdoNet.IntegrationTests;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.IntegrationTests;

/// <summary>
/// Live coverage for the Spark dialect quirks that the EF Core provider hit, checked here against
/// linq2db's own translation. Every case in this suite is one that generates SQL a warehouse
/// either rejects or — worse — silently evaluates differently, so none of it is provable offline.
/// </summary>
[Trait("Category", "Integration")]
public class Linq2DbDialectQuirksIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private const string Schema = "adodotnet_l2dbq_v1";
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private DatabricksConnection _connection = null!;

    private sealed class Item
    {
        [Column("run_id"), PrimaryKey(0)] public string RunId { get; set; } = "";

        [Column("id"), PrimaryKey(1)] public long Id { get; set; }

        [Column("name")] public string? Name { get; set; }

        [Column("qty")] public int Qty { get; set; }

        /// <summary>Nullable, so NULL semantics have something to work with.</summary>
        [Column("rating")] public int? Rating { get; set; }
    }

    private ITable<Item> ItemsTable(DataConnection db)
        => db.GetTable<Item>().TableName("items").SchemaName(Schema).ServerName(Catalog);

    private IQueryable<Item> Items(DataConnection db)
        => ItemsTable(db).Where(i => i.RunId == _runId);

    public async ValueTask InitializeAsync()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            return;
        }

        _connection = IntegrationConfig.CreateConnection();
        await _connection.OpenAsync();
        await IntegrationConfig.EnsureVersionedSchemaAsync(
            _connection,
            Schema,
            $"CREATE TABLE IF NOT EXISTS {Catalog}.{Schema}.items " +
            "(run_id STRING, id BIGINT, name STRING, qty INT, rating INT)");
        await ExecuteAsync(
            $"INSERT INTO {Catalog}.{Schema}.items VALUES " +
            $"('{_runId}', 1, 'alpha', 5, 3), ('{_runId}', 2, 'beta', 7, NULL), " +
            $"('{_runId}', 3, 'gamma', 9, 4)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await IntegrationConfig.DeleteRunRowsAsync(_connection, Schema, _runId, "items");
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
    public void String_concatenation_runs_server_side()
    {
        // With linq2db's default '+' style this failed outright with
        // DATATYPE_MISMATCH.BINARY_OP_WRONG_TYPE.
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var labels = Items(db).OrderBy(i => i.Id).Select(i => i.Name + "-x").ToList();

        Assert.Equal(["alpha-x", "beta-x", "gamma-x"], labels);
    }

    [IntegrationFact]
    public void String_concatenation_casts_a_non_string_operand()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var labels = Items(db).OrderBy(i => i.Id).Select(i => i.Name + "-" + i.Id).ToList();

        Assert.Equal(["alpha-1", "beta-2", "gamma-3"], labels);
    }

    [IntegrationFact]
    public void String_concatenation_is_usable_in_a_predicate()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var names = Items(db).Where(i => i.Name + "!" == "beta!").Select(i => i.Name).ToList();

        Assert.Equal(["beta"], names);
    }

    [IntegrationFact]
    public void Concatenating_a_null_yields_the_other_operand()
    {
        // linq2db wraps operands in COALESCE(x, ''), matching .NET's `null + "x" == "x"`.
        // Spark's own '||' would return NULL instead.
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var labels = Items(db)
            .Where(i => i.Id == 2)
            .Select(i => i.Rating.ToString() + "!")
            .ToList();

        Assert.Equal(["!"], labels);
    }

    [IntegrationFact]
    public void Quoted_literals_survive_spark_escaping()
    {
        // Spark reads '' as two adjacent literals, so doubling would drop the quote rather than
        // escape it; the mapping schema uses backslashes.
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var labels = Items(db).Where(i => i.Id == 1).Select(i => i.Name + "'s").ToList();

        Assert.Equal(["alpha's"], labels);
    }

    [IntegrationFact]
    public void Skip_without_take_emits_a_bare_offset()
    {
        // Databricks accepts OFFSET without LIMIT, so no 'LIMIT ALL' workaround is needed here
        // (EF Core's default emits a BIGINT limit, which Databricks rejects).
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var rest = Items(db).OrderBy(i => i.Id).Skip(1).Select(i => i.Name).ToList();

        Assert.Equal(["beta", "gamma"], rest);
    }

    [IntegrationFact]
    public void Integral_aggregates_narrow_to_their_clr_type()
    {
        // Databricks widens COUNT and SUM to BIGINT and AVG to DOUBLE. linq2db's value
        // converters narrow them on read, so unlike EF Core no CAST has to be generated —
        // this test exists to catch that changing.
        using var db = DatabricksTools.CreateDataConnection(_connection);

        Assert.Equal(3, Items(db).Count());
        Assert.Equal(3L, Items(db).LongCount());
        Assert.Equal(21, Items(db).Sum(i => i.Qty));
        Assert.Equal(7, Items(db).Average(i => i.Qty));
    }

    [IntegrationFact]
    public void Grouped_integral_aggregates_narrow_to_their_clr_type()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var grouped = Items(db)
            .GroupBy(i => i.Name)
            .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(i => i.Qty) })
            .OrderBy(x => x.Key)
            .ToList();

        Assert.Equal(["alpha", "beta", "gamma"], grouped.Select(x => x.Key));
        Assert.All(grouped, x => Assert.Equal(1, x.Count));
        Assert.Equal([5, 7, 9], grouped.Select(x => x.Total));
    }

    [IntegrationFact]
    public void Null_comparisons_and_coalesce_translate()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        Assert.Equal(["beta"], Items(db).Where(i => i.Rating == null).Select(i => i.Name).ToList());
        Assert.Equal(
            ["alpha", "gamma"],
            Items(db).Where(i => i.Rating != null).OrderBy(i => i.Name).Select(i => i.Name).ToList());
        Assert.Equal([3, 0, 4], Items(db).OrderBy(i => i.Id).Select(i => i.Rating ?? 0).ToList());
    }

    [IntegrationFact]
    public void Nulls_sort_first_when_ascending()
    {
        // Spark's default is NULLS FIRST ascending / NULLS LAST descending — the inverse of
        // PostgreSQL. linq2db emits no explicit NULLS clause, so this is what callers get.
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var ascending = Items(db).OrderBy(i => i.Rating).ThenBy(i => i.Id).Select(i => i.Rating).ToList();
        var descending = Items(db).OrderByDescending(i => i.Rating).ThenBy(i => i.Id).Select(i => i.Rating).ToList();

        Assert.Equal([null, 3, 4], ascending);
        Assert.Equal([4, 3, null], descending);
    }
}
