using GlutenFree.Databricks.AdoNet;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.Tests;

/// <summary>A minimal model used to exercise SQL generation without a warehouse.</summary>
public class Order
{
    public long Id { get; set; }

    public string Customer { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public bool Shipped { get; set; }

    public DateTime PlacedAt { get; set; }

    public List<OrderLine> Lines { get; } = [];
}

/// <summary>A dependent entity, so collection navigations can be exercised.</summary>
public class OrderLine
{
    public long Id { get; set; }

    public long OrderId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public Order? Order { get; set; }
}

/// <summary>Test context wired to the Databricks provider.</summary>
public class TestContext(DbContextOptions<TestContext> options) : DbContext(options)
{
    public const string ConnectionString =
        "Host=https://adb-1.azuredatabricks.net;WarehouseId=wh1;Token=dapi123;Catalog=main;Schema=sales";

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    /// <summary>Creates a context over the given connection string.</summary>
    public static TestContext Create(
        string? connectionString = null,
        Action<Infrastructure.DatabricksDbContextOptionsBuilder>? databricksOptions = null)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseDatabricks(connectionString ?? ConnectionString, databricksOptions)
            .Options;

        return new TestContext(options);
    }

    /// <summary>Creates a context over an existing connection.</summary>
    public static TestContext Create(DatabricksConnection connection)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseDatabricks(connection)
            .Options;

        return new TestContext(options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(o => o.Id);
            // Databricks cannot report store-generated values back to EF, so keys are
            // client-generated (see planning/efcore-provider-plan.md §2.2).
            b.Property(o => o.Id).ValueGeneratedNever();
            b.Property(o => o.Amount).HasColumnType("DECIMAL(18, 2)");
        });

        modelBuilder.Entity<OrderLine>(b =>
        {
            b.ToTable("order_lines");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.HasOne(l => l.Order).WithMany(o => o.Lines).HasForeignKey(l => l.OrderId);
        });
    }
}
