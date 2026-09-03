using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>
/// Live coverage for <c>GroupBy</c> beyond the single-key/single-aggregate case: composite keys,
/// computed keys, <c>HAVING</c>, nullable keys and ordering by an aggregate. These shapes each
/// generate structurally different SQL, so they are worth proving against a real warehouse
/// rather than assuming the relational defaults hold.
/// </summary>
[Trait("Category", "Integration")]
public class EfCoreGroupingIntegrationTests : WidgetFixture
{
    [IntegrationFact]
    public async Task Group_by_a_composite_key_translates()
    {
        await using var context = CreateContext();

        var grouped = await Widgets(context)
            .GroupBy(w => new { w.Active, HasRating = w.Rating != null })
            .Select(g => new { g.Key.Active, g.Key.HasRating, Count = g.Count() })
            .ToListAsync();

        var ordered = grouped.OrderBy(x => x.Active).ThenBy(x => x.HasRating).ToList();

        // alpha (active, rated), beta (inactive, unrated), gamma (active, unrated).
        Assert.Equal(3, ordered.Count);
        Assert.Equal((false, false, 1), (ordered[0].Active, ordered[0].HasRating, ordered[0].Count));
        Assert.Equal((true, false, 1), (ordered[1].Active, ordered[1].HasRating, ordered[1].Count));
        Assert.Equal((true, true, 1), (ordered[2].Active, ordered[2].HasRating, ordered[2].Count));
    }

    [IntegrationFact]
    public async Task Group_by_a_computed_key_translates()
    {
        // The key is an expression, so it has to be repeated in both SELECT and GROUP BY.
        await using var context = CreateContext();

        var grouped = await Widgets(context)
            .GroupBy(w => w.Name.Substring(0, 1))
            .Select(g => new { Initial = g.Key, Count = g.Count() })
            .OrderBy(x => x.Initial)
            .ToListAsync();

        Assert.Equal(["a", "b", "g"], grouped.Select(x => x.Initial));
        Assert.All(grouped, x => Assert.Equal(1, x.Count));
    }

    [IntegrationFact]
    public async Task Having_filters_groups_server_side()
    {
        await using var context = CreateContext();

        var keys = await Widgets(context)
            .GroupBy(w => w.Active)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync();

        Assert.Equal([true], keys);
    }

    [IntegrationFact]
    public async Task Having_on_an_aggregate_other_than_count_translates()
    {
        await using var context = CreateContext();

        var keys = await Widgets(context)
            .GroupBy(w => w.Active)
            .Where(g => g.Sum(w => w.Price) > 30m)
            .Select(g => g.Key)
            .ToListAsync();

        // active: 10.50 + 30.25 = 40.75; inactive: 20.00.
        Assert.Equal([true], keys);
    }

    [IntegrationFact]
    public async Task Group_by_projects_min_max_and_average()
    {
        await using var context = CreateContext();

        var grouped = await Widgets(context)
            .GroupBy(w => w.Active)
            .Select(g => new
            {
                g.Key,
                Min = g.Min(w => w.Price),
                Max = g.Max(w => w.Price),
                Average = g.Average(w => w.Price),
            })
            .OrderBy(x => x.Key)
            .ToListAsync();

        Assert.Equal(2, grouped.Count);
        Assert.Equal(20.00m, grouped[0].Min);
        Assert.Equal(20.00m, grouped[0].Max);
        Assert.Equal(20.00m, grouped[0].Average);
        Assert.Equal(10.50m, grouped[1].Min);
        Assert.Equal(30.25m, grouped[1].Max);
        Assert.Equal(20.375m, grouped[1].Average);
    }

    [IntegrationFact]
    public async Task Group_by_a_nullable_key_puts_all_nulls_in_one_group()
    {
        await using var context = CreateContext();

        var grouped = await Widgets(context)
            .GroupBy(w => w.Rating)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();

        Assert.Equal(2, grouped.Count);
        Assert.Equal(2, Assert.Single(grouped, x => x.Key is null).Count);
        Assert.Equal(1, Assert.Single(grouped, x => x.Key == 5).Count);
    }

    [IntegrationFact]
    public async Task Group_by_orders_by_an_aggregate()
    {
        await using var context = CreateContext();

        var grouped = await Widgets(context)
            .GroupBy(w => w.Active)
            .Select(g => new { g.Key, Total = g.Sum(w => w.Price) })
            .OrderByDescending(x => x.Total)
            .ToListAsync();

        Assert.Equal([40.75m, 20.00m], grouped.Select(x => x.Total));
    }

    [IntegrationFact]
    public async Task Group_by_applies_filters_before_and_after_grouping()
    {
        await using var context = CreateContext();

        var grouped = await Widgets(context)
            .Where(w => w.Price > 15m)
            .GroupBy(w => w.Active)
            .Where(g => g.Count() == 1)
            .Select(g => new { g.Key, Total = g.Sum(w => w.Price) })
            .OrderBy(x => x.Key)
            .ToListAsync();

        Assert.Equal(2, grouped.Count);
        Assert.Equal(20.00m, grouped[0].Total);
        Assert.Equal(30.25m, grouped[1].Total);
    }

    [IntegrationFact]
    public async Task Group_by_counts_a_distinct_column()
    {
        await using var context = CreateContext();

        var counts = await Widgets(context)
            .GroupBy(w => w.Active)
            .Select(g => new { g.Key, Names = g.Select(w => w.Name).Distinct().Count() })
            .OrderBy(x => x.Key)
            .ToListAsync();

        Assert.Equal([1, 2], counts.Select(x => x.Names));
    }
}
