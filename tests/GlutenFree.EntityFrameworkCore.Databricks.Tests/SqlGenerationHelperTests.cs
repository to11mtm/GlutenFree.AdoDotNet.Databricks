using GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Renders a query to SQL, for the literal-escaping assertions below.</summary>
    private static string Sql<T>(Func<TestContext, IQueryable<T>> query)
    {
        using var context = TestContext.Create();
        return query(context).ToQueryString();
    }

    [Fact]
    public void String_literals_use_spark_backslash_escaping()
    {
        // Doubling the quote ('') is read by Spark as two adjacent literals concatenated,
        // which silently drops the quote; backslash escaping is the correct form.
        var sql = Sql(c => c.Orders.Where(o => o.Customer == "it's"));

        Assert.Contains(@"'it\'s'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'it''s'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Backslashes_in_string_literals_are_escaped()
    {
        var sql = Sql(c => c.Orders.Where(o => o.Customer == @"a\b"));

        Assert.Contains(@"'a\\b'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Char_arguments_render_as_string_literals()
    {
        var sql = Sql(c => c.Orders.Select(o => o.Customer.PadLeft(6, '.')));

        Assert.Contains("lpad(`o`.`Customer`, 6, '.')", sql, StringComparison.Ordinal);
    }
}
