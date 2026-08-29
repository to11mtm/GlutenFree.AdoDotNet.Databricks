using LinqToDB.Internal.DataProvider.Translation;
using LinqToDB.Linq.Translation;

namespace Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// Member translator for Databricks SQL. Uses linq2db's defaults for v1;
/// Databricks-specific date/string function translations can be added incrementally.
/// </summary>
public sealed class DatabricksMemberTranslator : ProviderMemberTranslatorDefault
{
    /// <inheritdoc />
    protected override IMemberTranslator CreateDateMemberTranslator()
        => new DateFunctionsTranslator();

    private sealed class DateFunctionsTranslator : DateFunctionsTranslatorBase
    {
    }
}
