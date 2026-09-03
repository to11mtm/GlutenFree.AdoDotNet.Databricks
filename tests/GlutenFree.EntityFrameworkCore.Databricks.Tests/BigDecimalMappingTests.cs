using GlutenFree.Databricks.AdoNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace GlutenFree.EntityFrameworkCore.Databricks.Tests;

/// <summary>
/// Covers the arbitrary-precision decimal mapping and the warning raised when a
/// <see cref="decimal" /> is pointed at a column it cannot hold.
/// </summary>
public class BigDecimalMappingTests
{
    private class Money
    {
        public long Id { get; set; }

        public DatabricksDecimal Exact { get; set; }

        public decimal Narrow { get; set; }
    }

    private class MoneyContext(DbContextOptions<MoneyContext> options, string narrowColumnType)
        : DbContext(options)
    {
        public DbSet<Money> Amounts => Set<Money>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Money>(b =>
            {
                b.ToTable("money");
                b.HasKey(m => m.Id);
                b.Property(m => m.Id).ValueGeneratedNever();
                b.Property(m => m.Exact).HasColumnType("DECIMAL(38, 10)");
                b.Property(m => m.Narrow).HasColumnType(narrowColumnType);
            });
    }

    private static MoneyContext CreateContext(
        string narrowColumnType = "DECIMAL(18, 2)",
        ILoggerFactory? loggerFactory = null)
    {
        var builder = new DbContextOptionsBuilder<MoneyContext>()
            .UseDatabricks(TestContext.ConnectionString);

        if (loggerFactory is not null)
        {
            builder = builder.UseLoggerFactory(loggerFactory);
        }

        return new MoneyContext(builder.Options, narrowColumnType);
    }

    [Fact]
    public void DatabricksDecimal_maps_to_the_declared_decimal_column()
    {
        using var context = CreateContext();

        var mapping = context.Model
            .FindEntityType(typeof(Money))!
            .FindProperty(nameof(Money.Exact))!
            .GetRelationalTypeMapping();

        Assert.Equal(typeof(DatabricksDecimal), mapping.ClrType);
        Assert.Equal("DECIMAL(38,10)", mapping.StoreType);
    }

    [Fact]
    public void DatabricksDecimal_defaults_to_the_maximum_precision()
    {
        // Databricks' own DECIMAL default is (10, 0), which would silently truncate.
        var mapping = new Storage.Internal.DatabricksDecimalTypeMapping();

        Assert.Equal("DECIMAL(38,18)", mapping.StoreType);
    }

    [Fact]
    public void DatabricksDecimal_literals_use_the_canonical_form()
    {
        var mapping = new Storage.Internal.DatabricksDecimalTypeMapping();

        var literal = mapping.GenerateSqlLiteral(
            DatabricksDecimal.Parse("1234567890123456789012345678.1234567890"));

        Assert.Equal("1234567890123456789012345678.1234567890", literal);
    }

    [Fact]
    public void Decimal_on_a_wide_column_warns()
    {
        var logger = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logger).SetMinimumLevel(LogLevel.Warning));

        using var context = CreateContext("DECIMAL(38, 0)", factory);
        _ = context.Model;

        Assert.Contains(logger.Messages, m => m.Contains("Money.Narrow", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains(nameof(DatabricksDecimal), StringComparison.Ordinal));
    }

    [Fact]
    public void Decimal_on_a_narrow_column_does_not_warn()
    {
        var logger = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logger).SetMinimumLevel(LogLevel.Warning));

        using var context = CreateContext("DECIMAL(18, 2)", factory);
        _ = context.Model;

        Assert.DoesNotContain(logger.Messages, m => m.Contains("Money.Narrow", StringComparison.Ordinal));
    }

    /// <summary>Collects log messages so warnings can be asserted on.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
