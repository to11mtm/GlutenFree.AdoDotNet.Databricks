using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace GlutenFree.EntityFrameworkCore.Databricks.Metadata.Conventions;

/// <summary>Builds the model-building convention set for Databricks.</summary>
/// <remarks>
/// Databricks/Delta has no server-side default or identity generation that EF can read back
/// after an insert, so <see cref="ValueGenerationConvention" /> is replaced with one that marks
/// every property <see cref="Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never" /> and
/// keys are expected to be client-generated. See <c>planning/efcore-provider-plan.md</c> §2.2.
/// </remarks>
public class DatabricksConventionSetBuilder(
    ProviderConventionSetBuilderDependencies dependencies,
    RelationalConventionSetBuilderDependencies relationalDependencies)
    : RelationalConventionSetBuilder(dependencies, relationalDependencies)
{
    /// <inheritdoc />
    public override ConventionSet CreateConventionSet()
    {
        var conventionSet = base.CreateConventionSet();

        conventionSet.Replace<ValueGenerationConvention>(
            new DatabricksValueGenerationConvention(Dependencies, RelationalDependencies));

        return conventionSet;
    }
}
