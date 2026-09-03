using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>
/// Live coverage for the Phase 1 query translations: every assertion here exists because the
/// SQL has to be accepted by a real warehouse, not just look plausible offline.
/// </summary>
[Trait("Category", "Integration")]
public class EfCoreTranslationIntegrationTests : WidgetFixture
{
    [IntegrationFact]
    public async Task String_concatenation_uses_the_pipe_operator()
    {
        // Spark's '+' coerces operands to numbers, so a '+' here would yield NULL.
        await using var context = CreateContext();

        var labels = await Widgets(context)
            .OrderBy(w => w.Id)
            .Select(w => w.Name + "-" + w.Id)
            .ToListAsync();

        Assert.Equal(["alpha-1", "beta-2", "gamma-3"], labels);
    }

    [IntegrationFact]
    public async Task String_concatenation_is_usable_in_a_predicate()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => w.Name + "!" == "beta!")
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta"], names);
    }

    [IntegrationFact]
    public async Task Date_arithmetic_translates_to_timestampadd()
    {
        await using var context = CreateContext();

        var shifted = await Widgets(context)
            .OrderBy(w => w.Id)
            .Select(w => w.CreatedAt.AddDays(1))
            .ToListAsync();

        Assert.All(shifted, d => Assert.Equal(2, d.Day));
    }

    [IntegrationFact]
    public async Task Date_arithmetic_is_usable_in_a_predicate()
    {
        await using var context = CreateContext();

        var count = await Widgets(context)
            .Where(w => w.CreatedAt.AddMonths(-1) < w.CreatedAt)
            .CountAsync();

        Assert.Equal(3, count);
    }

    [IntegrationFact]
    public async Task Date_parts_translate()
    {
        await using var context = CreateContext();

        var years = await Widgets(context)
            .Select(w => w.CreatedAt.Year)
            .Distinct()
            .ToListAsync();

        Assert.Equal([2026], years);
    }

    [IntegrationFact]
    public async Task Set_operations_translate()
    {
        await using var context = CreateContext();

        var union = await Widgets(context).Where(w => w.Active)
            .Union(Widgets(context).Where(w => w.Price > 25m))
            .Select(w => w.Name)
            .OrderBy(n => n)
            .ToListAsync();

        Assert.Equal(["alpha", "gamma"], union);

        var except = await Widgets(context).Where(w => w.Active)
            .Except(Widgets(context).Where(w => w.Price > 25m))
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["alpha"], except);
    }

    [IntegrationFact]
    public async Task Distinct_with_ordering_translates()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Select(w => w.Name)
            .Distinct()
            .OrderByDescending(n => n)
            .ToListAsync();

        Assert.Equal(["gamma", "beta", "alpha"], names);
    }

    [IntegrationFact]
    public async Task In_list_translates()
    {
        await using var context = CreateContext();
        var wanted = new[] { 1L, 3L };

        var names = await Widgets(context)
            .Where(w => wanted.Contains(w.Id))
            .OrderBy(w => w.Id)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["alpha", "gamma"], names);
    }

    [IntegrationFact]
    public async Task Like_translates()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => EF.Functions.Like(w.Name, "b%"))
            .ToListAsync();

        Assert.Single(names);
        Assert.Equal("beta", names[0].Name);
    }

    [IntegrationFact]
    public async Task Group_by_aggregates_translate()
    {
        await using var context = CreateContext();

        var grouped = await Widgets(context)
            .GroupBy(w => w.Active)
            .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(w => w.Price) })
            .OrderBy(x => x.Key)
            .ToListAsync();

        Assert.Equal(2, grouped.Count);
        Assert.Equal(1, grouped[0].Count);
        Assert.Equal(20.00m, grouped[0].Total);
        Assert.Equal(2, grouped[1].Count);
        Assert.Equal(40.75m, grouped[1].Total);
    }

    [IntegrationFact]
    public async Task Long_count_stays_a_bigint()
    {
        await using var context = CreateContext();

        Assert.Equal(3L, await Widgets(context).LongCountAsync());
    }

    [IntegrationFact]
    public async Task Math_functions_translate()
    {
        await using var context = CreateContext();

        var rounded = await Widgets(context)
            .OrderBy(w => w.Id)
            .Select(w => Math.Round(w.Price))
            .ToListAsync();

        Assert.Equal([11m, 20m, 30m], rounded);
    }

    [IntegrationFact]
    public async Task String_functions_translate()
    {
        await using var context = CreateContext();

        var result = await Widgets(context)
            .Where(w => w.Name.Contains("amm"))
            .Select(w => new { Upper = w.Name.ToUpper(), Len = w.Name.Length, Cut = w.Name.Substring(1, 2) })
            .SingleAsync();

        Assert.Equal("GAMMA", result.Upper);
        Assert.Equal(5, result.Len);
        Assert.Equal("am", result.Cut);
    }

    [IntegrationFact]
    public async Task Padding_and_static_concat_translate()
    {
        await using var context = CreateContext();

        var result = await Widgets(context)
            .Where(w => w.Id == 2)
            .Select(w => new
            {
                Left = w.Name.PadLeft(6, '.'),
                Right = w.Name.PadRight(6, '.'),
                Joined = string.Concat(w.Name, "/", w.Name),
            })
            .SingleAsync();

        Assert.Equal("..beta", result.Left);
        Assert.Equal("beta..", result.Right);
        Assert.Equal("beta/beta", result.Joined);
    }

    [IntegrationFact]
    public async Task IndexOf_is_zero_based()
    {
        await using var context = CreateContext();

        var found = await Widgets(context).Where(w => w.Id == 3).Select(w => w.Name.IndexOf("mm")).SingleAsync();
        var missing = await Widgets(context).Where(w => w.Id == 3).Select(w => w.Name.IndexOf("zz")).SingleAsync();

        Assert.Equal(2, found);
        Assert.Equal(-1, missing);
    }

    [IntegrationFact]
    public async Task Timestamp_ntz_round_trips_without_a_time_zone_shift()
    {
        // TIMESTAMP_NTZ carries no zone, so the wall-clock value must survive unchanged and
        // arrive as Unspecified rather than being rebased into local/UTC.
        await using var context = CreateContext();

        var created = await Widgets(context)
            .Where(w => w.Id == 1)
            .Select(w => w.CreatedAt)
            .SingleAsync();

        Assert.Equal(new DateTime(2026, 3, 1, 8, 0, 0), created);
        Assert.Equal(DateTimeKind.Unspecified, created.Kind);
    }

    [IntegrationFact]
    public async Task Quoted_literals_survive_spark_escaping()
    {
        // Spark reads '' as two adjacent literals, so the wrong escaping would silently drop
        // the quote (or fail to parse) rather than producing "alpha's".
        await using var context = CreateContext();

        var possessive = await Widgets(context)
            .Where(w => w.Id == 1)
            .Select(w => w.Name + "'s")
            .SingleAsync();

        Assert.Equal("alpha's", possessive);
    }

    [IntegrationFact]
    public async Task Backslash_literals_survive_spark_escaping()
    {
        await using var context = CreateContext();

        var escaped = await Widgets(context)
            .Where(w => w.Id == 1)
            .Select(w => w.Name + @"\x")
            .SingleAsync();

        Assert.Equal(@"alpha\x", escaped);
    }

    [IntegrationFact]
    public async Task Decimal_scale_survives_the_round_trip()
    {
        await using var context = CreateContext();

        var prices = await Widgets(context).OrderBy(w => w.Id).Select(w => w.Price).ToListAsync();

        Assert.Equal([10.50m, 20.00m, 30.25m], prices);
    }
}
