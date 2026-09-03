using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>
/// Live coverage for <c>SaveChanges</c>. Inserts, updates and deletes are the part of the
/// provider that has to reason about Databricks' transaction model, so all of it is exercised
/// against a real warehouse over both transports.
/// </summary>
[Trait("Category", "Integration")]
public class EfCoreSaveChangesIntegrationTests : WidgetFixture
{
    private static int s_nextId = 1000;

    /// <summary>
    /// Keys are client-generated (Databricks cannot report a store-generated value back), so
    /// tests allocate their own. Ids start well above the seeded 1-3.
    /// </summary>
    private static long NextId() => Interlocked.Increment(ref s_nextId);

    private Widget NewWidget(string name, decimal price = 1.00m, bool active = true)
        => new()
        {
            RunId = RunId,
            Id = NextId(),
            Name = name,
            Price = price,
            Active = active,
            CreatedAt = new DateTime(2026, 3, 2, 12, 0, 0),
            BigValue = DatabricksDecimal.Parse("1.0000000001"),
        };

    [IntegrationFact]
    public async Task Insert_round_trips()
    {
        await using var context = CreateContext();

        var widget = NewWidget("inserted", 42.75m);
        context.Widgets.Add(widget);

        Assert.Equal(1, await context.SaveChangesAsync());

        await using var verify = CreateContext();
        var stored = await Widgets(verify).SingleAsync(w => w.Id == widget.Id);

        Assert.Equal("inserted", stored.Name);
        Assert.Equal(42.75m, stored.Price);
        Assert.True(stored.Active);
        Assert.Equal(new DateTime(2026, 3, 2, 12, 0, 0), stored.CreatedAt);
    }

    [IntegrationFact]
    public async Task Insert_of_several_entities_is_a_single_atomic_batch()
    {
        // With no ambient transaction the provider wraps the batch in BEGIN ATOMIC ... END;,
        // which is what lets a multi-row SaveChanges work at all over the stateless REST
        // transport — EF would otherwise demand a transaction it cannot open.
        await using var context = CreateContext();

        var widgets = new[] { NewWidget("batch-a"), NewWidget("batch-b"), NewWidget("batch-c") };
        context.Widgets.AddRange(widgets);

        Assert.Equal(3, await context.SaveChangesAsync());

        await using var verify = CreateContext();
        var names = await Widgets(verify)
            .Where(w => w.Name.StartsWith("batch-"))
            .OrderBy(w => w.Name)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["batch-a", "batch-b", "batch-c"], names);
    }

    [IntegrationFact]
    public async Task Update_round_trips()
    {
        await using var context = CreateContext();

        var widget = NewWidget("to-update", 5.00m);
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        widget.Name = "updated";
        widget.Price = 6.25m;
        widget.Active = false;

        Assert.Equal(1, await context.SaveChangesAsync());

        await using var verify = CreateContext();
        var stored = await Widgets(verify).SingleAsync(w => w.Id == widget.Id);

        Assert.Equal("updated", stored.Name);
        Assert.Equal(6.25m, stored.Price);
        Assert.False(stored.Active);
    }

    [IntegrationFact]
    public async Task Delete_round_trips()
    {
        await using var context = CreateContext();

        var widget = NewWidget("to-delete");
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        context.Widgets.Remove(widget);
        Assert.Equal(1, await context.SaveChangesAsync());

        await using var verify = CreateContext();
        Assert.False(await Widgets(verify).AnyAsync(w => w.Id == widget.Id));
    }

    [IntegrationFact]
    public async Task Insert_update_and_delete_in_one_save()
    {
        await using var seedContext = CreateContext();
        var toUpdate = NewWidget("mixed-update", 1.00m);
        var toDelete = NewWidget("mixed-delete");
        seedContext.Widgets.AddRange(toUpdate, toDelete);
        await seedContext.SaveChangesAsync();

        await using var context = CreateContext();
        var tracked = await Widgets(context).Where(w => w.Name.StartsWith("mixed-")).ToListAsync();

        var updating = tracked.Single(w => w.Name == "mixed-update");
        updating.Price = 99.99m;
        context.Widgets.Remove(tracked.Single(w => w.Name == "mixed-delete"));
        var inserted = NewWidget("mixed-insert");
        context.Widgets.Add(inserted);

        Assert.Equal(3, await context.SaveChangesAsync());

        await using var verify = CreateContext();
        var remaining = await Widgets(verify)
            .Where(w => w.Name.StartsWith("mixed-"))
            .OrderBy(w => w.Name)
            .Select(w => new { w.Name, w.Price })
            .ToListAsync();

        Assert.Equal(["mixed-insert", "mixed-update"], remaining.Select(r => r.Name));
        Assert.Equal(99.99m, remaining.Single(r => r.Name == "mixed-update").Price);
        Assert.False(await Widgets(verify).AnyAsync(w => w.Id == toDelete.Id));
    }

    [IntegrationFact]
    public async Task A_failing_statement_rolls_the_whole_batch_back()
    {
        // The second row's price does not fit DECIMAL(18, 2). Because the batch is one atomic
        // compound statement, the first row must not survive the failure either.
        await using var context = CreateContext();

        var good = NewWidget("atomic-good");
        var bad = NewWidget("atomic-bad", 12345678901234567.89m);
        context.Widgets.AddRange(good, bad);

        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync());

        await using var verify = CreateContext();
        Assert.False(await Widgets(verify).AnyAsync(w => w.Name.StartsWith("atomic-")));
    }

    [IntegrationFact]
    public async Task Auto_transactions_can_be_turned_off_for_non_atomic_saves()
    {
        // The escape hatch for tables without Delta's catalogManaged feature, which cannot be
        // written to inside a compound statement: each command goes out on its own instead.
        await using var context = CreateContext();
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

        context.Widgets.AddRange(NewWidget("loose-a"), NewWidget("loose-b"));

        Assert.Equal(2, await context.SaveChangesAsync());

        await using var verify = CreateContext();
        var names = await Widgets(verify)
            .Where(w => w.Name.StartsWith("loose-"))
            .OrderBy(w => w.Name)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(["loose-a", "loose-b"], names);
    }

    [IntegrationFact]
    public async Task Save_round_trips_arbitrary_precision_decimals()
    {
        await using var context = CreateContext();

        var widget = NewWidget("big-value");
        widget.BigValue = DatabricksDecimal.Parse("1234567890123456789012345678.1234567890");
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        await using var verify = CreateContext();
        var stored = await Widgets(verify).SingleAsync(w => w.Id == widget.Id);

        Assert.Equal("1234567890123456789012345678.1234567890", stored.BigValue.ToString());
    }

    [IntegrationFact]
    public async Task Save_round_trips_nulls()
    {
        await using var context = CreateContext();

        var widget = NewWidget("nullable");
        widget.Description = null;
        widget.Rating = null;
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        widget.Description = "set";
        widget.Rating = 7;
        await context.SaveChangesAsync();

        await using var verify = CreateContext();
        var stored = await Widgets(verify).SingleAsync(w => w.Id == widget.Id);

        Assert.Equal("set", stored.Description);
        Assert.Equal(7, stored.Rating);
    }
}
