using GlutenFree.Databricks.AdoNet.IntegrationTests;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.IntegrationTests;

/// <summary>
/// Live coverage for <c>DECIMAL</c> columns wider than a .NET <see cref="decimal" />.
/// Databricks allows <c>DECIMAL(38, s)</c>, which carries up to 38 significant digits against
/// <see cref="decimal" />'s ~28, so those columns must be mapped to
/// <see cref="DatabricksDecimal" /> to round-trip losslessly.
/// </summary>
[Trait("Category", "Integration")]
public class Linq2DbBigDecimalIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private const string Schema = "adodotnet_l2dbd_v1";
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private DatabricksConnection _connection = null!;

    /// <summary>The seeded value: 38 significant digits, more than a decimal can hold.</summary>
    private const string WideValue = "1234567890123456789012345678.1234567890";

    private sealed class Amount
    {
        [Column("run_id"), PrimaryKey(0)] public string RunId { get; set; } = "";

        [Column("id"), PrimaryKey(1)] public long Id { get; set; }

        [Column("value")] public DatabricksDecimal Value { get; set; }
    }

    /// <summary>The same table mapped to a <see cref="decimal" />, which cannot hold it.</summary>
    private sealed class NarrowAmount
    {
        [Column("run_id"), PrimaryKey(0)] public string RunId { get; set; } = "";

        [Column("id"), PrimaryKey(1)] public long Id { get; set; }

        [Column("value")] public decimal Value { get; set; }
    }

    private ITable<Amount> AmountsTable(DataConnection db)
        => db.GetTable<Amount>().TableName("amounts").SchemaName(Schema).ServerName(Catalog);

    private IQueryable<Amount> Amounts(DataConnection db)
        => AmountsTable(db).Where(a => a.RunId == _runId);

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
            $"CREATE TABLE IF NOT EXISTS {Catalog}.{Schema}.amounts " +
            "(run_id STRING, id BIGINT, value DECIMAL(38,10))");
        await using var seed = _connection.CreateCommand();
        seed.CommandText =
            $"INSERT INTO {Catalog}.{Schema}.amounts VALUES " +
            $"(:run_id, 1, {WideValue}), (:run_id, 2, 0.0000000001)";
        seed.Parameters.AddWithValue("run_id", _runId);
        await seed.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await IntegrationConfig.DeleteRunRowsAsync(_connection, Schema, _runId, "amounts");
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    [IntegrationFact]
    public void Wide_decimals_materialize_losslessly()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var value = Amounts(db).Single(a => a.Id == 1).Value;

        Assert.Equal(WideValue, value.ToString());
    }

    [IntegrationFact]
    public void Wide_decimals_round_trip_through_an_insert()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);
        var written = DatabricksDecimal.Parse("-9999999999999999999999999999.9999999999");

        AmountsTable(db).Insert(() => new Amount { RunId = _runId, Id = 3, Value = written });

        Assert.Equal(written.ToString(), Amounts(db).Single(a => a.Id == 3).Value.ToString());
    }

    [IntegrationFact]
    public void Wide_decimals_can_be_compared_server_side()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);
        var threshold = DatabricksDecimal.Parse("1.0");

        var ids = Amounts(db).Where(a => a.Value > threshold).Select(a => a.Id).ToList();

        Assert.Equal([1L], ids);
    }

    [IntegrationFact]
    public void Narrow_decimals_still_map_to_decimal()
    {
        using var db = DatabricksTools.CreateDataConnection(_connection);

        var value = db.GetTable<NarrowAmount>()
            .TableName("amounts").SchemaName(Schema).ServerName(Catalog)
            .Single(a => a.RunId == _runId && a.Id == 2)
            .Value;

        Assert.Equal(0.0000000001m, value);
    }

    [IntegrationFact]
    public void A_decimal_property_overflows_on_a_wide_value()
    {
        // Documents the failure mode that DatabricksDecimal exists to avoid: the value only
        // overflows for rows that actually use the extra digits, so it fails in production
        // rather than in testing.
        using var db = DatabricksTools.CreateDataConnection(_connection);

        Assert.Throws<OverflowException>(() =>
            db.GetTable<NarrowAmount>()
                .TableName("amounts").SchemaName(Schema).ServerName(Catalog)
                .Single(a => a.RunId == _runId && a.Id == 1));
    }
}
