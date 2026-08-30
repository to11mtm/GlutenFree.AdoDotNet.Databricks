namespace GlutenFree.Databricks.AdoNet.Thrift.Tests;

public class ThriftTransportOptionsTests
{
    [Fact]
    public void Providing_both_token_and_oauth_credentials_is_rejected()
    {
        var options = new ThriftTransportOptions
        {
            Token = "dapiXYZ",
            OAuthClientId = "client",
            OAuthClientSecret = "secret",
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new ThriftStatementTransport("https://x.databricks.net", "/sql/1.0/warehouses/abc", options));
        Assert.Contains("exactly one credential form", ex.Message);
    }

    [Fact]
    public void Missing_credentials_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ThriftStatementTransport(
                "https://x.databricks.net", "/sql/1.0/warehouses/abc", new ThriftTransportOptions()));
        Assert.Contains("personal access token", ex.Message);
    }

    [Fact]
    public void Plaintext_http_host_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ThriftStatementTransport(
                "http://x.databricks.net", "/sql/1.0/warehouses/abc",
                new ThriftTransportOptions { Token = "t" }));
        Assert.Contains("https", ex.Message);
    }
}
