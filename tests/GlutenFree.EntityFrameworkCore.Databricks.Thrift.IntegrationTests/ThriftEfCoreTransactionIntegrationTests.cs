using System.Runtime.CompilerServices;
using GlutenFree.Databricks.AdoNet.IntegrationTests;
using GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.Thrift.IntegrationTests;

/// <summary>
/// Live coverage for <c>SaveChanges</c> inside a caller-started transaction. Interactive
/// transactions are session state, so they only exist over Thrift; the batch factory drops the
/// <c>BEGIN ATOMIC</c> wrapper in that case and lets the transaction provide atomicity instead.
/// </summary>
/// <remarks>
/// Opt-in with <c>DATABRICKS_TRANSACTIONS=1</c>, for the same reason as
/// <c>ThriftTransactionIntegrationTests</c> in the ADO.NET project: not every workspace tier has
/// multi-statement transactions available.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ThriftEfCoreTransactionIntegrationTests : WidgetFixture
{
    private static int s_nextId = 5000;

    private Widget NewWidget(string name)
        => new()
        {
            RunId = RunId,
            Id = Interlocked.Increment(ref s_nextId),
            Name = name,
            Price = 1.00m,
            Active = true,
            CreatedAt = new DateTime(2026, 3, 3, 12, 0, 0),
        };

    [TransactionIntegrationFact]
    public async Task Committing_persists_the_saved_entities()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        context.Widgets.AddRange(NewWidget("txn-commit-a"), NewWidget("txn-commit-b"));
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        await using var verify = CreateContext();
        var count = await Widgets(verify).CountAsync(w => w.Name.StartsWith("txn-commit-"));

        Assert.Equal(2, count);
    }

    [TransactionIntegrationFact]
    public async Task Rolling_back_discards_the_saved_entities()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        context.Widgets.AddRange(NewWidget("txn-rollback-a"), NewWidget("txn-rollback-b"));
        await context.SaveChangesAsync();
        await transaction.RollbackAsync();

        await using var verify = CreateContext();

        Assert.False(await Widgets(verify).AnyAsync(w => w.Name.StartsWith("txn-rollback-")));
    }

    /// <summary>A fact that also requires <c>DATABRICKS_TRANSACTIONS=1</c>.</summary>
    private sealed class TransactionIntegrationFactAttribute : FactAttribute
    {
        public TransactionIntegrationFactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!IntegrationConfig.IsConfigured)
            {
                Skip = "Set DATABRICKS_HOST, DATABRICKS_TOKEN and DATABRICKS_WAREHOUSE_ID to run integration tests.";
            }
            else if (Environment.GetEnvironmentVariable("DATABRICKS_TRANSACTIONS") != "1")
            {
                Skip = "Set DATABRICKS_TRANSACTIONS=1 to run transaction integration tests; they require a "
                    + "workspace where multi-statement transactions and catalog-managed tables are available.";
            }

            Timeout = 300_000;
        }
    }
}
