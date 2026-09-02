using GlutenFree.Databricks.AdoNet;
using GlutenFree.EntityFrameworkCore.Databricks.Infrastructure.Internal;
using GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Tests;

/// <summary>Covers <c>UseDatabricks</c> and the options extension it configures.</summary>
public class OptionsPlumbingTests
{
    private static DatabricksOptionsExtension Extension(DbContextOptions options)
        => options.FindExtension<DatabricksOptionsExtension>()
            ?? throw new InvalidOperationException("The Databricks extension was not registered.");

    [Fact]
    public void UseDatabricks_registers_the_provider_extension()
    {
        var options = new DbContextOptionsBuilder()
            .UseDatabricks(TestContext.ConnectionString)
            .Options;

        var extension = Extension(options);

        Assert.True(extension.Info.IsDatabaseProvider);
        Assert.Equal(TestContext.ConnectionString, extension.ConnectionString);
    }

    [Fact]
    public void UseDatabricks_accepts_an_existing_connection()
    {
        using var connection = new DatabricksConnection(TestContext.ConnectionString);

        var options = new DbContextOptionsBuilder()
            .UseDatabricks(connection)
            .Options;

        Assert.Same(connection, Extension(options).Connection);
    }

    [Fact]
    public void Provider_name_is_reported_for_the_context()
    {
        using var context = TestContext.Create();

        Assert.Equal(
            typeof(DatabricksOptionsExtension).Assembly.GetName().Name,
            context.Database.ProviderName);
    }

    [Fact]
    public void Catalog_and_schema_options_are_recorded()
    {
        var options = new DbContextOptionsBuilder()
            .UseDatabricks(TestContext.ConnectionString, o => o.UseCatalog("main").UseSchema("analytics"))
            .Options;

        var extension = Extension(options);

        Assert.Equal("main", extension.Catalog);
        Assert.Equal("analytics", extension.Schema);
    }

    [Fact]
    public void Options_extension_is_immutable_when_configured()
    {
        var original = new DatabricksOptionsExtension();

        var withCatalog = original.WithCatalog("main");

        Assert.Null(original.Catalog);
        Assert.Equal("main", withCatalog.Catalog);
        Assert.NotSame(original, withCatalog);
    }

    [Fact]
    public void Command_timeout_flows_through_the_relational_base()
    {
        var options = new DbContextOptionsBuilder()
            .UseDatabricks(TestContext.ConnectionString, o => o.CommandTimeout(123))
            .Options;

        Assert.Equal(123, Extension(options).CommandTimeout);
    }

    [Fact]
    public void Catalog_and_schema_options_override_the_connection_string()
    {
        using var context = TestContext.Create(
            "Host=https://adb-1.azuredatabricks.net;WarehouseId=wh1;Token=dapi123;Catalog=main;Schema=sales",
            o => o.UseCatalog("other_catalog").UseSchema("other_schema"));

        var connection = (DatabricksConnection)context.Database.GetDbConnection();

        Assert.Equal("other_catalog", connection.Catalog);
        Assert.Equal("other_schema", connection.Database);
    }

    [Fact]
    public void The_relational_connection_is_the_databricks_one()
    {
        using var context = TestContext.Create();

        var connection = context.GetService<IRelationalConnection>();

        Assert.IsType<DatabricksRelationalConnection>(connection);
        Assert.IsType<DatabricksConnection>(connection.DbConnection);
    }

    [Fact]
    public void Transactions_do_not_advertise_savepoint_support()
    {
        using var context = TestContext.Create();

        Assert.IsType<DatabricksTransactionFactory>(context.GetService<IRelationalTransactionFactory>());
    }
}
