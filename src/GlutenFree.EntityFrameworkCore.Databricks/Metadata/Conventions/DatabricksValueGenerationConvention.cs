using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace GlutenFree.EntityFrameworkCore.Databricks.Metadata.Conventions;

/// <summary>
/// Marks every property as <see cref="ValueGenerated.Never" />.
/// </summary>
/// <remarks>
/// The relational default gives integer and <see cref="Guid" /> keys
/// <see cref="ValueGenerated.OnAdd" />, which tells EF the store will produce the value and hand
/// it back after the insert. Databricks has no <c>RETURNING</c>/<c>OUTPUT</c> clause, so nothing
/// can be read back and such a key would silently stay at its CLR default in the tracked entity.
/// Failing to *generate* is better than failing to *retrieve*: keys are client-supplied here, and
/// <see cref="Internal.DatabricksModelValidator" /> rejects any property that is explicitly
/// configured as store-generated. See <c>planning/efcore-provider-plan.md</c> §2.2.
/// </remarks>
public class DatabricksValueGenerationConvention(
    ProviderConventionSetBuilderDependencies dependencies,
    RelationalConventionSetBuilderDependencies relationalDependencies)
    : RelationalValueGenerationConvention(dependencies, relationalDependencies)
{
    /// <inheritdoc />
    protected override ValueGenerated? GetValueGenerated(IConventionProperty property)
        => ValueGenerated.Never;
}
