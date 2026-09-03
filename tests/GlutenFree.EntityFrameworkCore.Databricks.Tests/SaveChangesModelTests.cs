using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GlutenFree.EntityFrameworkCore.Databricks.Tests;

/// <summary>
/// Covers the model-level guarantees <c>SaveChanges</c> depends on: keys are client-generated,
/// and anything that would need a value read back from the store is refused up front rather than
/// failing silently at runtime.
/// </summary>
public class SaveChangesModelTests
{
    private class Thing
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Version { get; set; }
    }

    /// <summary>
    /// EF caches the built model per (context type, options), so each model variant needs its own
    /// context type — otherwise whichever test runs first decides the model all of them see.
    /// </summary>
    private abstract class ThingContextBase(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Thing> Things => Set<Thing>();

        protected abstract void ConfigureThing(ModelBuilder modelBuilder);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>(b =>
            {
                b.ToTable("things");
                b.HasKey(t => t.Id);
            });

            ConfigureThing(modelBuilder);
        }
    }

    /// <summary>Nothing configured: the conventions alone decide value generation.</summary>
    private sealed class ConventionThingContext(DbContextOptions<ConventionThingContext> options)
        : ThingContextBase(options)
    {
        protected override void ConfigureThing(ModelBuilder modelBuilder)
        {
        }
    }

    /// <summary>A key explicitly opted into store generation.</summary>
    private sealed class IdentityThingContext(DbContextOptions<IdentityThingContext> options)
        : ThingContextBase(options)
    {
        protected override void ConfigureThing(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Thing>().Property(t => t.Id).ValueGeneratedOnAdd();
    }

    /// <summary>A concurrency token.</summary>
    private sealed class ConcurrencyThingContext(DbContextOptions<ConcurrencyThingContext> options)
        : ThingContextBase(options)
    {
        protected override void ConfigureThing(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Thing>().Property(t => t.Version).IsConcurrencyToken();
    }

    private static TContext Create<TContext>(Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
        => factory(
            new DbContextOptionsBuilder<TContext>()
                .UseDatabricks(TestContext.ConnectionString)
                .Options);

    [Fact]
    public void An_integer_key_is_not_store_generated_by_convention()
    {
        // The relational default would be ValueGenerated.OnAdd, which assumes the store hands the
        // value back — Databricks has no way to do that.
        using var context = Create<ConventionThingContext>(o => new ConventionThingContext(o));

        var id = context.Model.FindEntityType(typeof(Thing))!.FindProperty(nameof(Thing.Id))!;

        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);
    }

    [Fact]
    public void An_explicitly_store_generated_property_is_rejected()
    {
        using var context = Create<IdentityThingContext>(o => new IdentityThingContext(o));

        var exception = Assert.Throws<InvalidOperationException>(() => _ = context.Model);

        Assert.Contains("Thing.Id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ValueGeneratedNever", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_concurrency_token_is_rejected()
    {
        using var context = Create<ConcurrencyThingContext>(o => new ConcurrencyThingContext(o));

        var exception = Assert.Throws<InvalidOperationException>(() => _ = context.Model);

        Assert.Contains("Thing.Version", exception.Message, StringComparison.Ordinal);
        Assert.Contains("concurrency token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_explains_that_migrations_are_not_supported()
    {
        using var context = Create<ConventionThingContext>(o => new ConventionThingContext(o));

        var exception = Assert.Throws<NotSupportedException>(() => context.Database.Migrate());

        Assert.Contains("does not support EF Core Migrations", exception.Message, StringComparison.Ordinal);
        Assert.Contains("EnsureCreated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrateAsync_explains_that_migrations_are_not_supported()
    {
        await using var context = Create<ConventionThingContext>(o => new ConventionThingContext(o));

        await Assert.ThrowsAsync<NotSupportedException>(() => context.Database.MigrateAsync());
    }

    [Fact]
    public void GenerateScript_explains_that_migrations_are_not_supported()
    {
        using var context = Create<ConventionThingContext>(o => new ConventionThingContext(o));

        var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        Assert.Throws<NotSupportedException>(() => migrator.GenerateScript());
        Assert.Throws<NotSupportedException>(() => migrator.HasPendingModelChanges());
    }
}
