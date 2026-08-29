using System.Data.SqlTypes;

namespace GlutenFree.Databricks.AdoNet.IntegrationTests;

/// <summary>
/// Live coverage of numeric type fidelity: integer boundary values, floating-point
/// specials, and DECIMAL precision/scale extremes including DECIMAL(38,0).
/// </summary>
[Trait("Category", "Integration")]
public sealed class NumericTypesIntegrationTests : IAsyncLifetime
{
    private const string Catalog = "workspace";
    private readonly string _schema = $"adonet_num_{Guid.NewGuid():N}";
    private DatabricksConnection _connection = null!;

    public async Task InitializeAsync()
    {
        if (!IntegrationConfig.IsConfigured)
        {
            return;
        }

        _connection = new DatabricksConnection(IntegrationConfig.ConnectionString);
        await _connection.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await ExecuteAsync($"DROP SCHEMA IF EXISTS {Catalog}.{_schema} CASCADE");
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

    private async Task<DatabricksDataReader> QueryAsync(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader;
    }

    [IntegrationFact]
    public async Task Integer_boundary_values_roundtrip()
    {
        await using var reader = await QueryAsync("""
            SELECT
              CAST(127 AS TINYINT), CAST(-128 AS TINYINT),
              CAST(32767 AS SMALLINT), CAST(-32768 AS SMALLINT),
              CAST(2147483647 AS INT), CAST(-2147483648 AS INT),
              CAST(9223372036854775807 AS BIGINT), CAST(-9223372036854775808 AS BIGINT)
            """);

        Assert.Equal(sbyte.MaxValue, reader.GetSByte(0));
        Assert.Equal(sbyte.MinValue, reader.GetSByte(1));
        Assert.Equal(short.MaxValue, reader.GetInt16(2));
        Assert.Equal(short.MinValue, reader.GetInt16(3));
        Assert.Equal(int.MaxValue, reader.GetInt32(4));
        Assert.Equal(int.MinValue, reader.GetInt32(5));
        Assert.Equal(long.MaxValue, reader.GetInt64(6));
        Assert.Equal(long.MinValue, reader.GetInt64(7));
    }

    [IntegrationFact]
    public async Task Floating_point_specials_roundtrip()
    {
        await using var reader = await QueryAsync("""
            SELECT
              CAST('NaN' AS DOUBLE), CAST('Infinity' AS DOUBLE), CAST('-Infinity' AS DOUBLE),
              CAST('NaN' AS FLOAT), CAST('Infinity' AS FLOAT),
              CAST(1.7976931348623157E308 AS DOUBLE),
              CAST(-1.7976931348623157E308 AS DOUBLE),
              CAST(3.4028235E38 AS FLOAT),
              CAST(0.1 AS DOUBLE)
            """);

        Assert.True(double.IsNaN(reader.GetDouble(0)));
        Assert.True(double.IsPositiveInfinity(reader.GetDouble(1)));
        Assert.True(double.IsNegativeInfinity(reader.GetDouble(2)));
        Assert.True(float.IsNaN(reader.GetFloat(3)));
        Assert.True(float.IsPositiveInfinity(reader.GetFloat(4)));
        Assert.Equal(double.MaxValue, reader.GetDouble(5));
        Assert.Equal(double.MinValue, reader.GetDouble(6));
        Assert.Equal(float.MaxValue, reader.GetFloat(7));
        Assert.Equal(0.1, reader.GetDouble(8));
    }

    [IntegrationFact]
    public async Task Decimal_precision_and_scale_extremes_roundtrip()
    {
        const string max38 = "99999999999999999999999999999999999999";      // DECIMAL(38,0) max
        const string maxScale = "0.99999999999999999999999999999999999999"; // DECIMAL(38,38) max
        const string mixed = "9999999999999999999.9999999999999999999";     // DECIMAL(38,19)

        await using var reader = await QueryAsync($"""
            SELECT
              CAST('{max38}' AS DECIMAL(38,0)),
              CAST('-{max38}' AS DECIMAL(38,0)),
              CAST('{maxScale}' AS DECIMAL(38,38)),
              CAST('{mixed}' AS DECIMAL(38,19)),
              CAST('12345678901234.56' AS DECIMAL(18,2)),
              CAST(0 AS DECIMAL(38,0))
            """);

        // p > 28 columns surface as SqlDecimal (lossless, full 38 digits).
        Assert.Equal(typeof(SqlDecimal), reader.GetFieldType(0));
        Assert.Equal(SqlDecimal.Parse(max38), reader.GetSqlDecimal(0));
        Assert.Equal(max38, reader.GetSqlDecimal(0).ToString());
        Assert.Equal(SqlDecimal.Parse("-" + max38), reader.GetSqlDecimal(1));
        Assert.Equal(SqlDecimal.Parse(maxScale), reader.GetSqlDecimal(2));
        Assert.Equal(SqlDecimal.Parse(mixed), reader.GetSqlDecimal(3));

        // GetDecimal must overflow loudly, never truncate silently.
        Assert.Throws<OverflowException>(() => reader.GetDecimal(0));

        // p <= 28 columns surface as System.Decimal directly.
        Assert.Equal(typeof(decimal), reader.GetFieldType(4));
        Assert.Equal(12345678901234.56m, reader.GetDecimal(4));

        Assert.Equal(SqlDecimal.Parse("0"), reader.GetSqlDecimal(5));
    }

    [IntegrationFact]
    public async Task Decimal_and_integer_parameters_roundtrip()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT :dec AS d, :big AS b, :tiny AS t, :neg AS n";
        command.Parameters.AddWithValue("dec", 1234.5678m);
        command.Parameters.AddWithValue("big", long.MaxValue);
        command.Parameters.AddWithValue("tiny", (sbyte)-5);
        command.Parameters.AddWithValue("neg", -0.001m);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(1234.5678m, reader.GetDecimal(0));
        Assert.Equal(long.MaxValue, reader.GetInt64(1));
        Assert.Equal(-5, Convert.ToInt32(reader.GetValue(2)));
        Assert.Equal(-0.001m, reader.GetDecimal(3));
    }

    [IntegrationFact]
    public async Task SqlDecimal_parameter_carries_full_38_digit_precision()
    {
        var huge = SqlDecimal.Parse("12345678901234567890123456789012345.678");

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT :huge AS h";
        command.Parameters.AddWithValue("huge", huge);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(huge, reader.GetSqlDecimal(0));
    }

    [IntegrationFact]
    public async Task Numeric_storage_roundtrip_through_table()
    {
        await ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {Catalog}.{_schema}");
        var table = $"{Catalog}.{_schema}.numerics";
        await ExecuteAsync(
            $"CREATE TABLE {table} (t TINYINT, s SMALLINT, i INT, b BIGINT, " +
            "f FLOAT, d DOUBLE, small_dec DECIMAL(18,4), big_dec DECIMAL(38,0), n INT)");

        // Store boundary values via parameters, read back via Arrow.
        await using (var insert = _connection.CreateCommand())
        {
            insert.CommandText =
                $"INSERT INTO {table} VALUES (:t, :s, :i, :b, :f, :d, :small_dec, CAST(:big_dec AS DECIMAL(38,0)), :n)";
            insert.Parameters.AddWithValue("t", sbyte.MinValue);
            insert.Parameters.AddWithValue("s", short.MaxValue);
            insert.Parameters.AddWithValue("i", int.MinValue);
            insert.Parameters.AddWithValue("b", long.MaxValue);
            insert.Parameters.AddWithValue("f", 1.5f);
            insert.Parameters.AddWithValue("d", -2.25);
            insert.Parameters.AddWithValue("small_dec", 1234.5678m);
            insert.Parameters.AddWithValue("big_dec", SqlDecimal.Parse("99999999999999999999999999999999999999"));
            insert.Parameters.AddWithValue("n", (object?)null);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        await using var reader = await QueryAsync($"SELECT * FROM {table}");

        Assert.Equal(sbyte.MinValue, reader.GetSByte(0));
        Assert.Equal(short.MaxValue, reader.GetInt16(1));
        Assert.Equal(int.MinValue, reader.GetInt32(2));
        Assert.Equal(long.MaxValue, reader.GetInt64(3));
        Assert.Equal(1.5f, reader.GetFloat(4));
        Assert.Equal(-2.25, reader.GetDouble(5));
        Assert.Equal(1234.5678m, reader.GetDecimal(6));
        Assert.Equal(
            SqlDecimal.Parse("99999999999999999999999999999999999999"),
            reader.GetSqlDecimal(7));
        Assert.True(reader.IsDBNull(8));
    }
}
