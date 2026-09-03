using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>
/// Live coverage for the bulk-update query operators. These are query-side (they do not go
/// through <c>SaveChanges</c>), so they belong with the Phase 1 query work.
/// </summary>
[Trait("Category", "Integration")]
public class EfCoreBulkOperationIntegrationTests : WidgetFixture
{
    [IntegrationFact]
    public async Task ExecuteUpdate_sets_a_column()
    {
        await using var context = CreateContext();

        var affected = await Widgets(context)
            .Where(w => w.Name == "beta")
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Price, 99.99m));

        Assert.Equal(1, affected);

        var price = await Widgets(context).Where(w => w.Name == "beta").Select(w => w.Price).SingleAsync();
        Assert.Equal(99.99m, price);
    }

    [IntegrationFact]
    public async Task ExecuteUpdate_can_reference_the_existing_value()
    {
        await using var context = CreateContext();

        var affected = await Widgets(context)
            .Where(w => w.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Name, w => w.Name + "-x"));

        Assert.Equal(2, affected);

        var names = await Widgets(context)
            .Where(w => w.Active)
            .OrderBy(w => w.Id)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["alpha-x", "gamma-x"], names);
    }

    [IntegrationFact]
    public async Task ExecuteDelete_removes_matching_rows()
    {
        await using var context = CreateContext();

        var affected = await Widgets(context)
            .Where(w => w.Price < 15m)
            .ExecuteDeleteAsync();

        Assert.Equal(1, affected);
        Assert.Equal(2, await Widgets(context).CountAsync());
    }
}
