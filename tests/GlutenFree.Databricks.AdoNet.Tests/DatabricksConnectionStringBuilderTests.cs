using GlutenFree.Databricks.AdoNet;

namespace GlutenFree.Databricks.AdoNet.Tests;

public class DatabricksConnectionStringBuilderTests
{
    private const string ValidPat =
        "Host=https://adb-123.4.azuredatabricks.net;WarehouseId=abc123;Token=dapiXYZ";

    [Fact]
    public void Parses_basic_pat_connection_string()
    {
        var b = new DatabricksConnectionStringBuilder(ValidPat);

        Assert.Equal("https://adb-123.4.azuredatabricks.net", b.Host);
        Assert.Equal("abc123", b.WarehouseId);
        Assert.Equal("dapiXYZ", b.Token);
        Assert.Equal(DatabricksAuthType.Pat, b.AuthType);
    }

    [Fact]
    public void Keywords_are_case_insensitive_and_accept_spaced_forms()
    {
        var b = new DatabricksConnectionStringBuilder(
            "host=https://x.databricks.net;Warehouse Id=w1;TOKEN=t;command timeout=90");

        Assert.Equal("w1", b.WarehouseId);
        Assert.Equal(90, b.CommandTimeout);
    }

    [Fact]
    public void Unknown_keyword_throws()
    {
        Assert.Throws<ArgumentException>(
            () => new DatabricksConnectionStringBuilder("Server=nope"));
    }

    [Fact]
    public void Defaults_are_applied()
    {
        var b = new DatabricksConnectionStringBuilder(ValidPat);

        Assert.Equal(0, b.CommandTimeout);
        Assert.Equal(30, b.ConnectTimeout);
        Assert.Equal(DatabricksResultFormat.Arrow, b.ResultFormat);
        Assert.Equal(DatabricksDisposition.Auto, b.Disposition);
        Assert.Equal(4, b.MaxRetries);
        Assert.Equal(500, b.RetryBaseDelay);
        Assert.True(b.Pooling);
    }

    [Theory]
    [InlineData("/sql/1.0/warehouses/abcdef123", "abcdef123")]
    [InlineData("/sql/1.0/warehouses/abcdef123/", "abcdef123")]
    [InlineData("plainid", "plainid")]
    public void EffectiveWarehouseId_parses_http_path(string httpPath, string expected)
    {
        var b = new DatabricksConnectionStringBuilder
        {
            Host = "https://x.databricks.net",
            HttpPath = httpPath,
        };

        Assert.Equal(expected, b.EffectiveWarehouseId);
    }

    [Fact]
    public void Explicit_warehouse_id_wins_over_http_path()
    {
        var b = new DatabricksConnectionStringBuilder
        {
            WarehouseId = "explicit",
            HttpPath = "/sql/1.0/warehouses/other",
        };

        Assert.Equal("explicit", b.EffectiveWarehouseId);
    }

    [Fact]
    public void ToDisplayString_redacts_secrets()
    {
        var b = new DatabricksConnectionStringBuilder(ValidPat + ";ClientSecret=shh");

        var display = b.ToDisplayString();

        Assert.DoesNotContain("dapiXYZ", display);
        Assert.DoesNotContain("shh", display);
        Assert.Contains("*****", display);
        Assert.Contains("abc123", display); // non-secrets preserved
    }

    [Fact]
    public void Validate_accepts_valid_pat_string()
    {
        var b = new DatabricksConnectionStringBuilder(ValidPat);
        b.Validate();
    }

    [Fact]
    public void Validate_accepts_valid_oauth_string()
    {
        var b = new DatabricksConnectionStringBuilder(
            "Host=https://x.databricks.net;WarehouseId=w;AuthType=OAuthM2M;ClientId=id;ClientSecret=secret");
        b.Validate();
    }

    [Theory]
    [InlineData("WarehouseId=w;Token=t", "Host")]
    [InlineData("Host=not a url;WarehouseId=w;Token=t", "Host")]
    [InlineData("Host=https://x.databricks.net;Token=t", "WarehouseId")]
    [InlineData("Host=https://x.databricks.net;WarehouseId=w", "Token")]
    [InlineData("Host=https://x.databricks.net;WarehouseId=w;AuthType=OAuthM2M;ClientId=id", "ClientSecret")]
    public void Validate_rejects_incomplete_strings(string connectionString, string expectedInMessage)
    {
        var b = new DatabricksConnectionStringBuilder(connectionString);

        var ex = Assert.Throws<ArgumentException>(b.Validate);
        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Fact]
    public void Invalid_enum_value_throws_with_valid_values_listed()
    {
        var b = new DatabricksConnectionStringBuilder(ValidPat + ";ResultFormat=Xml");

        var ex = Assert.Throws<ArgumentException>(() => b.ResultFormat);
        Assert.Contains("Arrow", ex.Message);
        Assert.Contains("Json", ex.Message);
    }

    [Fact]
    public void Negative_numeric_values_are_rejected()
    {
        var b = new DatabricksConnectionStringBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => b.CommandTimeout = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.MaxRetries = -5);
    }

    [Fact]
    public void Roundtrips_through_connection_string()
    {
        var b = new DatabricksConnectionStringBuilder
        {
            Host = "https://x.databricks.net",
            WarehouseId = "w1",
            AuthType = DatabricksAuthType.OAuthM2M,
            ClientId = "cid",
            ClientSecret = "cs",
            Catalog = "main",
            Schema = "default",
            ResultFormat = DatabricksResultFormat.Json,
            CommandTimeout = 120,
        };

        var parsed = new DatabricksConnectionStringBuilder(b.ConnectionString);

        Assert.Equal(DatabricksAuthType.OAuthM2M, parsed.AuthType);
        Assert.Equal("main", parsed.Catalog);
        Assert.Equal("default", parsed.Schema);
        Assert.Equal(DatabricksResultFormat.Json, parsed.ResultFormat);
        Assert.Equal(120, parsed.CommandTimeout);
    }
}
