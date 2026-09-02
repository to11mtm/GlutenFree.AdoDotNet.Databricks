using Microsoft.EntityFrameworkCore.Query;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>Supplies the Databricks member translators.</summary>
/// <remarks>
/// The relational base registers none, so everything a provider does not translate here falls
/// back to client evaluation (or fails, in a <c>WHERE</c> clause).
/// </remarks>
public class DatabricksMemberTranslatorProvider : RelationalMemberTranslatorProvider
{
    /// <summary>Creates the provider.</summary>
    public DatabricksMemberTranslatorProvider(RelationalMemberTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        var sqlExpressionFactory = dependencies.SqlExpressionFactory;

        AddTranslators(
        [
            new DatabricksStringMemberTranslator(sqlExpressionFactory),
            new DatabricksDateTimeMemberTranslator(sqlExpressionFactory),
        ]);
    }
}
