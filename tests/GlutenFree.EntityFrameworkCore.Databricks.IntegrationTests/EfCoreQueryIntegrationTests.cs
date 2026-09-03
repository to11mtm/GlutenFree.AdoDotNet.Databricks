using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>
/// Live coverage for the core query pipeline against a Databricks SQL warehouse: proves the
/// generated SQL is actually accepted and that results materialize.
/// </summary>
[Trait("Category", "Integration")]
public class EfCoreQueryIntegrationTests : WidgetFixture
{
    [IntegrationFact]
    public async Task Select_materializes_rows()
    {
        await using var context = CreateContext();

        var widgets = await Widgets(context)
            .OrderBy(w => w.Id)
            .ToListAsync();

        Assert.Equal(3, widgets.Count);
        Assert.Equal("alpha", widgets[0].Name);
        Assert.Equal(10.50m, widgets[0].Price);
        Assert.True(widgets[0].Active);
        Assert.False(widgets[1].Active);
    }

    [IntegrationFact]
    public async Task Where_with_parameters_filters_server_side()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => w.Price > 15m && w.Active)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["gamma"], names);
    }

    [IntegrationFact]
    public async Task Skip_and_take_paginate()
    {
        await using var context = CreateContext();

        var page = await Widgets(context)
            .OrderBy(w => w.Id)
            .Skip(1)
            .Take(1)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta"], page);
    }

    [IntegrationFact]
    public async Task Skip_without_take_uses_limit_all()
    {
        await using var context = CreateContext();

        var rest = await Widgets(context)
            .OrderBy(w => w.Id)
            .Skip(1)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["beta", "gamma"], rest);
    }

    [IntegrationFact]
    public async Task Aggregates_translate()
    {
        await using var context = CreateContext();

        var query = Widgets(context);

        Assert.Equal(3, await query.CountAsync());
        Assert.Equal(60.75m, await query.SumAsync(w => w.Price));
        Assert.Equal(30.25m, await query.MaxAsync(w => w.Price));
    }

    [IntegrationFact]
    public async Task String_translations_run_server_side()
    {
        await using var context = CreateContext();

        var names = await Widgets(context)
            .Where(w => w.Name.StartsWith("g"))
            .Select(w => w.Name.ToUpper())
            .ToListAsync();

        Assert.Equal(["GAMMA"], names);
    }
}
