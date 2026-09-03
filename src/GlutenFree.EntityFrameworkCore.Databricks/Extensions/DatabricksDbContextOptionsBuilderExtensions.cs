using System.Data.Common;
using GlutenFree.Databricks.AdoNet;
using GlutenFree.EntityFrameworkCore.Databricks.Infrastructure;
using GlutenFree.EntityFrameworkCore.Databricks.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Databricks-specific extension methods for <see cref="DbContextOptionsBuilder" />.
/// </summary>
public static class DatabricksDbContextOptionsBuilderExtensions
{
    /// <summary>Configures the context to connect to a Databricks SQL warehouse.</summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">A <see cref="DatabricksConnection" /> connection string.</param>
    /// <param name="databricksOptionsAction">An optional action to configure provider options.</param>
    public static DbContextOptionsBuilder UseDatabricks(
        this DbContextOptionsBuilder optionsBuilder,
        string? connectionString,
        Action<DatabricksDbContextOptionsBuilder>? databricksOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extension = (DatabricksOptionsExtension)GetOrCreateExtension(optionsBuilder)
            .WithConnectionString(connectionString);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        databricksOptionsAction?.Invoke(new DatabricksDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    /// <summary>Configures the context to use an existing <see cref="DatabricksConnection" />.</summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connection">
    /// An existing connection. If it is already open, EF will not close it.
    /// </param>
    /// <param name="databricksOptionsAction">An optional action to configure provider options.</param>
    public static DbContextOptionsBuilder UseDatabricks(
        this DbContextOptionsBuilder optionsBuilder,
        DbConnection connection,
        Action<DatabricksDbContextOptionsBuilder>? databricksOptionsAction = null)
        => optionsBuilder.UseDatabricks(connection, contextOwnsConnection: false, databricksOptionsAction);

    /// <summary>Configures the context to use an existing <see cref="DatabricksConnection" />.</summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connection">An existing connection.</param>
    /// <param name="contextOwnsConnection">
    /// <see langword="true" /> to have the context dispose the connection when it is disposed.
    /// </param>
    /// <param name="databricksOptionsAction">An optional action to configure provider options.</param>
    public static DbContextOptionsBuilder UseDatabricks(
        this DbContextOptionsBuilder optionsBuilder,
        DbConnection connection,
        bool contextOwnsConnection,
        Action<DatabricksDbContextOptionsBuilder>? databricksOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        var extension = (DatabricksOptionsExtension)GetOrCreateExtension(optionsBuilder)
            .WithConnection(connection, contextOwnsConnection);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        databricksOptionsAction?.Invoke(new DatabricksDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    /// <summary>Configures the context to connect to a Databricks SQL warehouse.</summary>
    public static DbContextOptionsBuilder<TContext> UseDatabricks<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string? connectionString,
        Action<DatabricksDbContextOptionsBuilder>? databricksOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseDatabricks(
            (DbContextOptionsBuilder)optionsBuilder, connectionString, databricksOptionsAction);

    /// <summary>Configures the context to use an existing <see cref="DatabricksConnection" />.</summary>
    public static DbContextOptionsBuilder<TContext> UseDatabricks<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        DbConnection connection,
        Action<DatabricksDbContextOptionsBuilder>? databricksOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseDatabricks(
            (DbContextOptionsBuilder)optionsBuilder, connection, databricksOptionsAction);

    /// <summary>Configures the context to use an existing <see cref="DatabricksConnection" />.</summary>
    public static DbContextOptionsBuilder<TContext> UseDatabricks<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        DbConnection connection,
        bool contextOwnsConnection,
        Action<DatabricksDbContextOptionsBuilder>? databricksOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseDatabricks(
            (DbContextOptionsBuilder)optionsBuilder, connection, contextOwnsConnection, databricksOptionsAction);

    private static DatabricksOptionsExtension GetOrCreateExtension(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.Options.FindExtension<DatabricksOptionsExtension>()
            ?? new DatabricksOptionsExtension();
}
