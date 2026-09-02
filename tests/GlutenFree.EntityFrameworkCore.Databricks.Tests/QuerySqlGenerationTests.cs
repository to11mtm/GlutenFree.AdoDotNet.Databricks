using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.Tests;

/// <summary>
/// Verifies the SQL the provider generates. These run entirely offline: EF compiles the query
/// and renders SQL without opening a connection.
/// </summary>
public class QuerySqlGenerationTests
{
    /// <summary>
    /// Renders a query to SQL. EF compiles and renders without opening a connection.
    /// </summary>
    private static string Sql<T>(Func<TestContext, IQueryable<T>> query)
    {
        using var context = TestContext.Create();
        return query(context).ToQueryString();
    }

    /// <summary>
    /// Compares generated SQL, normalizing line endings on both sides so the expectations
    /// (raw string literals, which carry the source file's line endings) are platform neutral.
    /// </summary>
    private static void AssertSql(string expected, string actual)
        => Assert.Equal(expected.ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));

    [Fact]
    public void Select_quotes_identifiers_with_backticks()
        => AssertSql(
            """
            SELECT `o`.`Id`, `o`.`Amount`, `o`.`Customer`, `o`.`PlacedAt`, `o`.`Shipped`
            FROM `orders` AS `o`
            """,
            Sql(c => c.Orders));

    [Fact]
    public void Take_generates_limit()
        => AssertSql(
            """
            -- p='10'
            SELECT `o`.`Id`, `o`.`Amount`, `o`.`Customer`, `o`.`PlacedAt`, `o`.`Shipped`
            FROM `orders` AS `o`
            LIMIT :p
            """,
            Sql(c => c.Orders.Take(10)));

    [Fact]
    public void Skip_and_take_generate_limit_then_offset()
        => AssertSql(
            """
            -- p1='10'
            -- p='5'
            SELECT `o`.`Id`, `o`.`Amount`, `o`.`Customer`, `o`.`PlacedAt`, `o`.`Shipped`
            FROM `orders` AS `o`
            ORDER BY `o`.`Id`
            LIMIT :p1
            OFFSET :p
            """,
            Sql(c => c.Orders.OrderBy(o => o.Id).Skip(5).Take(10)));

    [Fact]
    public void Skip_without_take_emits_limit_all()
        // Databricks rejects OFFSET without LIMIT, and the limit must be an INT expression,
        // so an unbounded skip uses the LIMIT ALL form rather than a large literal.
        => AssertSql(
            """
            -- p='5'
            SELECT `o`.`Id`, `o`.`Amount`, `o`.`Customer`, `o`.`PlacedAt`, `o`.`Shipped`
            FROM `orders` AS `o`
            ORDER BY `o`.`Id`
            LIMIT ALL
            OFFSET :p
            """,
            Sql(c => c.Orders.OrderBy(o => o.Id).Skip(5)));

    [Fact]
    public void String_methods_use_databricks_functions()
        => AssertSql(
            """
            SELECT `o`.`Id`, `o`.`Amount`, `o`.`Customer`, `o`.`PlacedAt`, `o`.`Shipped`
            FROM `orders` AS `o`
            WHERE startswith(`o`.`Customer`, 'a') AND length(`o`.`Customer`) > 3
            """,
            Sql(c => c.Orders.Where(o => o.Customer.StartsWith("a") && o.Customer.Length > 3)));

    [Fact]
    public void Date_parts_translate_to_databricks_functions()
        => AssertSql(
            """
            SELECT `o`.`Id`, `o`.`Amount`, `o`.`Customer`, `o`.`PlacedAt`, `o`.`Shipped`
            FROM `orders` AS `o`
            WHERE year(`o`.`PlacedAt`) = 2026
            """,
            Sql(c => c.Orders.Where(o => o.PlacedAt.Year == 2026)));

    [Fact]
    public void Count_is_cast_to_int()
        // Databricks' COUNT returns BIGINT while Queryable.Count materializes an int, so the
        // aggregate is cast to keep the reader's typed access valid.
        => AssertSql(
            """
            SELECT `o`.`Customer` AS `Key`, CAST(COUNT(*) AS INT) AS `Count`
            FROM `orders` AS `o`
            GROUP BY `o`.`Customer`
            """,
            Sql(c => c.Orders
                .GroupBy(o => o.Customer)
                .Select(g => new { g.Key, Count = g.Count() })));

    [Fact]
    public void Parameters_use_colon_markers()
    {
        var customer = "acme";

        AssertSql(
            """
            -- customer='acme'
            SELECT `o`.`Id`, `o`.`Amount`, `o`.`Customer`, `o`.`PlacedAt`, `o`.`Shipped`
            FROM `orders` AS `o`
            WHERE `o`.`Customer` = :customer AND `o`.`Shipped`
            """,
            Sql(c => c.Orders.Where(o => o.Customer == customer && o.Shipped)));
    }

    [Fact]
    public void Projection_selects_only_mapped_columns()
        => AssertSql(
            """
            SELECT `o`.`Id`, `o`.`Customer`
            FROM `orders` AS `o`
            WHERE `o`.`Amount` > 100.0
            """,
            Sql(c => c.Orders.Where(o => o.Amount > 100m).Select(o => new { o.Id, o.Customer })));

    [Fact]
    public void String_concatenation_uses_the_pipe_operator()
        // Spark's '+' is arithmetic only: it coerces operands to numbers and yields NULL.
        => AssertSql(
            """
            SELECT `o`.`Customer` || '!'
            FROM `orders` AS `o`
            """,
            Sql(c => c.Orders.Select(o => o.Customer + "!")));

    [Fact]
    public void Numeric_addition_still_uses_plus()
        => AssertSql(
            """
            SELECT `o`.`Amount` + 1.0
            FROM `orders` AS `o`
            """,
            Sql(c => c.Orders.Select(o => o.Amount + 1m)));

    [Fact]
    public void Date_arithmetic_uses_timestampadd()
        => AssertSql(
            """
            SELECT timestampadd(MONTH, 2, `o`.`PlacedAt`)
            FROM `orders` AS `o`
            """,
            Sql(c => c.Orders.Select(o => o.PlacedAt.AddMonths(2))));

    [Fact]
    public void Fractional_date_arithmetic_is_not_translated()
    {
        // timestampadd takes an integral amount; rather than truncating behind the caller's
        // back the translation is declined, leaving EF to evaluate it on the client.
        var sql = Sql(c => c.Orders.Select(o => o.PlacedAt.AddDays(1.5)));

        Assert.DoesNotContain("timestampadd", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Correlated_collections_avoid_apply()
    {
        // EF rewrites these into ROW_NUMBER() subqueries; Databricks has no APPLY, so if that
        // ever changes the generator's LATERAL fallback needs to be exercised instead.
        var sql = Sql(c => c.Orders.SelectMany(o => o.Lines.OrderBy(l => l.Id).Take(2)));

        Assert.DoesNotContain("APPLY", sql, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER()", sql, StringComparison.Ordinal);
    }
}
