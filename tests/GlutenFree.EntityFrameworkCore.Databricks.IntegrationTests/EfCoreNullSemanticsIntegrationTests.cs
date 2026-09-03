using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>
/// Live coverage for NULL handling. EF rewrites C# null semantics into SQL that assumes
/// three-valued logic plus <c>IS NULL</c>/<c>COALESCE</c>; this suite proves Spark agrees, and
/// pins down the two places it differs from most relational databases (NULL sort order, and
/// aggregates over an all-NULL set).
/// </summary>
[Trait("Category", "Integration")]
public class EfCoreNullSemanticsIntegrationTests : WidgetFixture
{
    [IntegrationFact]
    public async Task Comparison_to_null_becomes_is_null()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => w.Description == null)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta"], names);
    }

    [IntegrationFact]
    public async Task Comparison_to_not_null_becomes_is_not_null()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => w.Description != null)
            .OrderBy(w => w.Name)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["alpha", "gamma"], names);
    }

    [IntegrationFact]
    public async Task A_null_valued_parameter_is_compared_as_is_null()
    {
        // EF has to notice the captured value is null and emit IS NULL; '= NULL' is never true.
        await using var context = CreateContext();
        string? wanted = null;

        var names = await Widgets(context)
            .Where(w => w.Description == wanted)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta"], names);
    }

    [IntegrationFact]
    public async Task Inequality_still_matches_null_rows()
    {
        // C# says null != "first", so EF must widen this to (description IS NULL OR
        // description <> 'first') — a bare <> would silently drop beta.
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => w.Description != "first")
            .OrderBy(w => w.Name)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta", "gamma"], names);
    }

    [IntegrationFact]
    public async Task Coalesce_translates()
    {
        await using var context = CreateContext();

        var ratings = await Widgets(context)
            .OrderBy(w => w.Id)
            .Select(w => w.Rating ?? 0)
            .ToListAsync();

        Assert.Equal([5, 0, 0], ratings);
    }

    [IntegrationFact]
    public async Task Coalesce_is_usable_in_a_predicate()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => (w.Rating ?? 0) > 1)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["alpha"], names);
    }

    [IntegrationFact]
    public async Task IsNullOrEmpty_translates()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => string.IsNullOrEmpty(w.Description))
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta"], names);
    }

    [IntegrationFact]
    public async Task Nullable_HasValue_translates()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => w.Rating.HasValue)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["alpha"], names);
    }

    [IntegrationFact]
    public async Task Aggregates_over_a_nullable_column_skip_nulls()
    {
        await using var context = CreateContext();

        Assert.Equal(5, await Widgets(context).SumAsync(w => w.Rating));
        Assert.Equal(5, await Widgets(context).MaxAsync(w => w.Rating));
        Assert.Equal(3, await Widgets(context).CountAsync());
        Assert.Equal(1, await Widgets(context).CountAsync(w => w.Rating != null));
    }

    [IntegrationFact]
    public async Task Sum_over_an_all_null_set_is_zero_and_average_is_null()
    {
        // Spark's SUM/AVG ignore NULLs and return NULL for an empty input; EF's SumAsync
        // coalesces that to the CLR default, while AverageAsync over int? keeps the null.
        await using var context = CreateContext();

        var empty = Widgets(context).Where(w => w.Name == "beta");

        Assert.Equal(0, await empty.SumAsync(w => w.Rating));
        Assert.Null(await empty.AverageAsync(w => w.Rating));
    }

    [IntegrationFact]
    public async Task Nulls_sort_first_when_ascending()
    {
        // Spark's default is NULLS FIRST ascending / NULLS LAST descending, matching
        // PostgreSQL's inverse and SQL Server's ascending behaviour. EF emits no explicit
        // NULLS clause, so this is the ordering applications will actually see.
        await using var context = CreateContext();

        var ascending = await Widgets(context)
            .OrderBy(w => w.Rating).ThenBy(w => w.Id)
            .Select(w => w.Rating)
            .ToListAsync();

        var descending = await Widgets(context)
            .OrderByDescending(w => w.Rating).ThenBy(w => w.Id)
            .Select(w => w.Rating)
            .ToListAsync();

        Assert.Equal([null, null, 5], ascending);
        Assert.Equal([5, null, null], descending);
    }

    [IntegrationFact]
    public async Task Null_columns_materialize_as_null()
    {
        await using var context = CreateContext();

        var widget = await Widgets(context).SingleAsync(w => w.Id == 2);

        Assert.Null(widget.Description);
        Assert.Null(widget.Rating);
    }
}
