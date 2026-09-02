using GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Tests;

/// <summary>Covers Databricks identifier quoting and parameter marker syntax.</summary>
public class SqlGenerationHelperTests
{
    private static DatabricksSqlGenerationHelper CreateHelper()
        => new(new RelationalSqlGenerationHelperDependencies());

    [Fact]
    public void Identifiers_are_delimited_with_backticks()
        => Assert.Equal("`orders`", CreateHelper().DelimitIdentifier("orders"));

    [Fact]
    public void Backticks_inside_an_identifier_are_doubled()
        => Assert.Equal("`we``ird`", CreateHelper().DelimitIdentifier("we`ird"));

    [Fact]
    public void Escaping_an_identifier_doubles_backticks_without_quoting()
        => Assert.Equal("we``ird", CreateHelper().EscapeIdentifier("we`ird"));

    [Fact]
    public void Escaping_into_a_builder_matches_the_string_overload()
    {
        var helper = CreateHelper();
        var builder = new System.Text.StringBuilder("prefix ");

        helper.DelimitIdentifier(builder, "we`ird");

        Assert.Equal("prefix `we``ird`", builder.ToString());
    }

    [Fact]
    public void Parameters_use_a_colon_marker()
    {
        var helper = CreateHelper();

        Assert.Equal(":p0", helper.GenerateParameterName("p0"));
        Assert.Equal(":p0", helper.GenerateParameterNamePlaceholder("p0"));
    }

    [Fact]
    public void Schema_qualified_identifiers_quote_both_parts()
        => Assert.Equal("`sales`.`orders`", CreateHelper().DelimitIdentifier("orders", "sales"));
}
