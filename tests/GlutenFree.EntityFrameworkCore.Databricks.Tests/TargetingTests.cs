using System.Reflection;
using GlutenFree.EntityFrameworkCore.Databricks.Internal;

namespace GlutenFree.EntityFrameworkCore.Databricks.Tests;

/// <summary>
/// Guards the targeting decisions for the EF Core provider package (see
/// <c>planning/efcore-provider-plan.md</c> §1): the provider tracks the EF Core major
/// version, which pins both the EF dependency and the target framework.
/// </summary>
public class TargetingTests
{
    private static readonly Assembly s_provider =
        typeof(DatabricksLoggingDefinitions).Assembly;

    [Fact]
    public void Provider_targets_net10()
    {
        var targetFramework = s_provider
            .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()
            ?.FrameworkName;

        Assert.Equal(".NETCoreApp,Version=v10.0", targetFramework);
    }

    [Fact]
    public void Provider_references_ef_core_relational_10()
    {
        var efCore = s_provider
            .GetReferencedAssemblies()
            .SingleOrDefault(a => a.Name == "Microsoft.EntityFrameworkCore.Relational");

        Assert.NotNull(efCore);
        Assert.Equal(10, efCore.Version?.Major);
    }

    [Fact]
    public void Provider_version_major_is_the_ef_core_major()
    {
        // The package version is `10.<repo version>`, so a v0.3.0 tag publishes 10.0.3.0.
        // This guards the rule in the csproj against being flattened back to the repo version.
        Assert.Equal(10, s_provider.GetName().Version?.Major);
    }
}
