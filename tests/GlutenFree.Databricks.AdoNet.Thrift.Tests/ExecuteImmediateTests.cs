using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Thrift.Tests;

/// <summary>
/// Locks down the EXECUTE IMMEDIATE parameter emulation: marker resolution is delegated
/// to the server, values only ever appear inside escaped string literals, and hostile
/// names/types/values cannot escape the literal context.
/// </summary>
public class ExecuteImmediateTests
{
    private static string Build(string sql, params StatementParameter[] parameters)
        => ThriftStatementTransport.BuildExecuteImmediate(sql, parameters);

    [Fact]
    public void Wraps_statement_and_binds_typed_values()
    {
        var sql = Build(
            "SELECT :num AS n, :text AS t",
            new StatementParameter { Name = "num", Value = "42", Type = "BIGINT" },
            new StatementParameter { Name = "text", Value = "hello", Type = "STRING" });

        Assert.Equal(
            "EXECUTE IMMEDIATE 'SELECT :num AS n, :text AS t' " +
            "USING CAST('42' AS BIGINT) AS num, CAST('hello' AS STRING) AS text",
            sql);
    }

    [Fact]
    public void Null_values_bind_as_typed_nulls()
    {
        var sql = Build(
            "SELECT :v",
            new StatementParameter { Name = "v", Value = null, Type = "INT" });

        Assert.Equal("EXECUTE IMMEDIATE 'SELECT :v' USING CAST(NULL AS INT) AS v", sql);
    }

    [Fact]
    public void Missing_type_defaults_to_string()
    {
        var sql = Build("SELECT :v", new StatementParameter { Name = "v", Value = "x" });

        Assert.Equal("EXECUTE IMMEDIATE 'SELECT :v' USING CAST('x' AS STRING) AS v", sql);
    }

    [Fact]
    public void Decimal_type_with_precision_and_scale_is_allowed()
    {
        var sql = Build(
            "SELECT :d",
            new StatementParameter { Name = "d", Value = "12345.67", Type = "DECIMAL(10,2)" });

        Assert.Contains("CAST('12345.67' AS DECIMAL(10,2)) AS d", sql);
    }

    [Theory]
    [InlineData("it's safe; -- no injection", "it\\'s safe; -- no injection")]
    [InlineData("a\\'b", "a\\\\\\'b")]
    [InlineData("'; DROP TABLE users; --", "\\'; DROP TABLE users; --")]
    public void Values_are_escaped_inside_string_literals(string value, string expectedEscaped)
    {
        var sql = Build("SELECT :v", new StatementParameter { Name = "v", Value = value, Type = "STRING" });

        Assert.Equal($"EXECUTE IMMEDIATE 'SELECT :v' USING CAST('{expectedEscaped}' AS STRING) AS v", sql);
    }

    [Fact]
    public void Statement_text_quotes_are_escaped()
    {
        var sql = Build(
            "SELECT ':literal' AS quoted, :v",
            new StatementParameter { Name = "v", Value = "1", Type = "INT" });

        Assert.StartsWith("EXECUTE IMMEDIATE 'SELECT \\':literal\\' AS quoted, :v' USING ", sql);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("bad name")]
    [InlineData("x'y")]
    [InlineData("a;b")]
    public void Hostile_parameter_names_are_rejected(string name)
    {
        Assert.Throws<DatabricksException>(() =>
            Build("SELECT :v", new StatementParameter { Name = name, Value = "1" }));
    }

    [Theory]
    [InlineData("STRING) AS x FROM t; --")]
    [InlineData("INT'")]
    [InlineData("DECIMAL(10,2)) UNION SELECT password FROM users --")]
    [InlineData("DECIMAL(1234,5678)")]
    public void Hostile_type_names_are_rejected(string typeName)
    {
        Assert.Throws<DatabricksException>(() =>
            Build("SELECT :v", new StatementParameter { Name = "v", Value = "1", Type = typeName }));
    }
}
