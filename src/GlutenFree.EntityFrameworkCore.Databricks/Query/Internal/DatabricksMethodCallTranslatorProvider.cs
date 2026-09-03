using Microsoft.EntityFrameworkCore.Query;

namespace GlutenFree.EntityFrameworkCore.Databricks.Query.Internal;

/// <summary>Supplies the Databricks method-call translators.</summary>
public class DatabricksMethodCallTranslatorProvider : RelationalMethodCallTranslatorProvider
{
    /// <summary>Creates the provider.</summary>
    public DatabricksMethodCallTranslatorProvider(RelationalMethodCallTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        var sqlExpressionFactory = dependencies.SqlExpressionFactory;

        AddTranslators(
        [
            new DatabricksStringMethodTranslator(sqlExpressionFactory),
            new DatabricksDateTimeMethodTranslator(sqlExpressionFactory),
            new DatabricksMathTranslator(sqlExpressionFactory),
        ]);
    }
}
