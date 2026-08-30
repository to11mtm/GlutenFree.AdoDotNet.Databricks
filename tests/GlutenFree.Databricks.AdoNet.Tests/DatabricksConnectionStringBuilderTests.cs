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

    [Theory]
    [InlineData("/sql/protocolv1/o/12345/6789-cluster", true)]
    [InlineData("sql/protocolv1/o/12345/6789-cluster", true)]
    [InlineData("/SQL/ProtocolV1/o/12345/6789-cluster", true)]
    [InlineData("/sql/1.0/warehouses/abcdef123", false)]
    [InlineData("", false)]
    public void Detects_all_purpose_cluster_paths(string httpPath, bool expected)
    {
        var b = new DatabricksConnectionStringBuilder
        {
            Host = "https://x.databricks.net",
            HttpPath = httpPath,
        };

        Assert.Equal(expected, b.IsAllPurposeClusterPath);
    }

    [Fact]
    public void Cluster_path_yields_no_warehouse_id()
    {
        var b = new DatabricksConnectionStringBuilder
        {
            Host = "https://x.databricks.net",
            HttpPath = "/sql/protocolv1/o/12345/6789-cluster",
        };

        // A cluster id must never be mistaken for a warehouse id (the REST API
        // would send it as warehouse_id and fail with a confusing server error).
        Assert.Equal(string.Empty, b.EffectiveWarehouseId);
    }

    [Fact]
    public void Validate_accepts_cluster_http_path_without_warehouse_id()
    {
        var b = new DatabricksConnectionStringBuilder(
            "Host=https://x.databricks.net;HttpPath=/sql/protocolv1/o/12345/6789-cluster;Token=t");
        b.Validate();
    }

    [Fact]
    public void Opening_cluster_path_on_default_rest_transport_fails_with_guidance()
    {
        using var connection = new DatabricksConnection(
            "Host=https://x.databricks.net;HttpPath=/sql/protocolv1/o/12345/6789-cluster;Token=t");

        var ex = Assert.Throws<NotSupportedException>(connection.Open);
        Assert.Contains("UseThriftTransport", ex.Message);
        Assert.Contains("all-purpose cluster", ex.Message);
    }

    [Fact]
    public void Explicit_warehouse_id_beats_cluster_path_on_rest_transport()
    {
        // Documented precedence: WarehouseId wins over HttpPath, so the REST transport
        // must not reject the connection just because a cluster path is also present.
        // (Open fails later on network/auth, not with the cluster NotSupportedException.)
        using var connection = new DatabricksConnection(
            "Host=https://localhost:1;WarehouseId=w123;HttpPath=/sql/protocolv1/o/12345/6789-cluster;Token=t;ConnectTimeout=1");

        try
        {
            connection.Open();
        }
        catch (NotSupportedException)
        {
            Assert.Fail("REST transport rejected a cluster HttpPath despite an explicit WarehouseId.");
        }
        catch
        {
            // Expected: unreachable host / auth failure — precedence honored.
        }
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
