using System.ComponentModel;
using GlutenFree.EntityFrameworkCore.Databricks.Infrastructure.Internal;
using GlutenFree.EntityFrameworkCore.Databricks.Internal;
using GlutenFree.EntityFrameworkCore.Databricks.Metadata.Conventions;
using GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;
using GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;
using GlutenFree.EntityFrameworkCore.Databricks.Update.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Databricks-specific extension methods for <see cref="IServiceCollection" />.
/// </summary>
public static class DatabricksServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services required by the Databricks provider to an
    /// <see cref="IServiceCollection" />.
    /// </summary>
    /// <remarks>
    /// Calling this is not normally needed: it is only required when building an internal
    /// service provider for use with <c>DbContextOptionsBuilder.UseInternalServiceProvider</c>.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection AddEntityFrameworkDatabricks(this IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        new EntityFrameworkRelationalServicesBuilder(serviceCollection)
            .TryAdd<LoggingDefinitions, DatabricksLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<DatabricksOptionsExtension>>()
            .TryAdd<IRelationalTypeMappingSource, DatabricksTypeMappingSource>()
            .TryAdd<ISqlGenerationHelper, DatabricksSqlGenerationHelper>()
            .TryAdd<IProviderConventionSetBuilder, DatabricksConventionSetBuilder>()
            .TryAdd<IModelValidator, DatabricksModelValidator>()
            .TryAdd<IRelationalConnection>(p => p.GetRequiredService<IDatabricksRelationalConnection>())
            .TryAdd<IRelationalTransactionFactory, DatabricksTransactionFactory>()
            .TryAdd<IRelationalDatabaseCreator, DatabricksDatabaseCreator>()
            .TryAdd<IModificationCommandBatchFactory, DatabricksModificationCommandBatchFactory>()
            .TryAdd<IUpdateSqlGenerator, DatabricksUpdateSqlGenerator>()
            .TryAdd<IQuerySqlGeneratorFactory, DatabricksQuerySqlGeneratorFactory>()
            .TryAdd<IMethodCallTranslatorProvider, DatabricksMethodCallTranslatorProvider>()
            .TryAdd<IMemberTranslatorProvider, DatabricksMemberTranslatorProvider>()
            .TryAddProviderSpecificServices(
                b => b.TryAddScoped<IDatabricksRelationalConnection, DatabricksRelationalConnection>())
            .TryAddCoreServices();

        return serviceCollection;
    }
}
