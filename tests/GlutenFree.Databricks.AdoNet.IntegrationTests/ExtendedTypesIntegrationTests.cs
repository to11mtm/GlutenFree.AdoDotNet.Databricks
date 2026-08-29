namespace GlutenFree.Databricks.AdoNet.IntegrationTests;

/// <summary>
/// Live coverage of the remaining types from the Databricks SQL data type reference:
/// TIMESTAMP_NTZ, INTERVAL (year-month and day-time), MAP, VARIANT, CHAR/VARCHAR, and VOID/NULL.
/// GEOGRAPHY/GEOMETRY are intentionally out of scope (preview types, not broadly enabled).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExtendedTypesIntegrationTests : IAsyncLifetime
{
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
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
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
    public async Task Timestamp_ntz_roundtrips_as_datetime()
    {
        await using var reader = await QueryAsync(
            "SELECT TIMESTAMP_NTZ'2026-08-29 12:34:56.789', CAST(NULL AS TIMESTAMP_NTZ)");

        Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
        Assert.Equal(new DateTime(2026, 8, 29, 12, 34, 56, 789), reader.GetDateTime(0));
        Assert.True(reader.IsDBNull(1));
    }

    [IntegrationFact]
    public async Task Interval_types_surface_as_strings()
    {
        await using var reader = await QueryAsync("""
            SELECT
              INTERVAL '2-3' YEAR TO MONTH,
              INTERVAL '5 04:03:02.1' DAY TO SECOND,
              INTERVAL '7' YEAR,
              INTERVAL '90' MINUTE
            """);

        // v1 contract: intervals are strings; exact rendering may differ by wire format,
        // but the field values must be present and non-empty.
        for (var i = 0; i < 4; i++)
        {
            Assert.False(reader.IsDBNull(i), $"interval column {i} was null");
            Assert.False(string.IsNullOrEmpty(reader.GetString(i)), $"interval column {i} was empty");
        }

        Assert.Contains("2-3", reader.GetString(0));
    }

    [IntegrationFact]
    public async Task Map_surfaces_as_json_string()
    {
        await using var reader = await QueryAsync(
            "SELECT map('a', 1, 'b', 2), map()");

        Assert.Equal(typeof(string), reader.GetFieldType(0));
        var json = reader.GetString(0);
        Assert.Contains("\"a\"", json);
        Assert.Contains("1", json);
        Assert.Contains("\"b\"", json);

        var empty = reader.GetString(1);
        Assert.False(string.IsNullOrEmpty(empty));
    }

    [IntegrationFact]
    public async Task Nested_complex_types_surface_as_json_string()
    {
        await using var reader = await QueryAsync(
            "SELECT array(named_struct('k', 'x', 'v', array(1, 2)), named_struct('k', 'y', 'v', array(3)))");

        var json = reader.GetString(0);
        Assert.Contains("\"k\"", json);
        Assert.Contains("\"x\"", json);
        Assert.Contains("[1,2]", json.Replace(" ", ""));
    }

    [IntegrationFact]
    public async Task Variant_surfaces_as_json_string()
    {
        await using var reader = await QueryAsync(
            """SELECT parse_json('{"name": "alice", "tags": [1, 2]}')""");

        var json = reader.GetString(0);
        Assert.Contains("alice", json);
        Assert.Contains("tags", json);
    }

    [IntegrationFact]
    public async Task Char_and_varchar_surface_as_strings()
    {
        await using var reader = await QueryAsync(
            "SELECT CAST('ab' AS CHAR(5)), CAST('hello' AS VARCHAR(10))");

        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.StartsWith("ab", reader.GetString(0)); // CHAR may be blank-padded
        Assert.Equal("hello", reader.GetString(1));
    }

    [IntegrationFact]
    public async Task Void_null_literal_is_dbnull()
    {
        await using var reader = await QueryAsync("SELECT NULL, CAST(NULL AS VOID)");

        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
    }
}
