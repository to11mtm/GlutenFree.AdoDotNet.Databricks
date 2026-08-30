namespace GlutenFree.Databricks.AdoNet.Thrift.Tests;

public class HttpPathResolutionTests
{
    [Theory]
    [InlineData("/sql/protocolv1/o/12345/6789-cluster", "/sql/protocolv1/o/12345/6789-cluster")]
    [InlineData("sql/protocolv1/o/12345/6789-cluster", "/sql/protocolv1/o/12345/6789-cluster")]
    [InlineData("/sql/1.0/warehouses/abc", "/sql/1.0/warehouses/abc")]
    [InlineData("sql/1.0/warehouses/abc", "/sql/1.0/warehouses/abc")]
    public void Explicit_http_path_is_normalized_to_leading_slash(string httpPath, string expected)
    {
        Assert.Equal(expected, DatabricksConnectionThriftExtensions.ResolveHttpPath(httpPath, "ignored"));
    }

    [Fact]
    public void Empty_http_path_derives_warehouse_path()
    {
        Assert.Equal(
            "/sql/1.0/warehouses/abc123",
            DatabricksConnectionThriftExtensions.ResolveHttpPath("", "abc123"));
    }
}
