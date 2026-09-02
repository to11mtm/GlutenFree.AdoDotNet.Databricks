using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// SQL syntax rules for Databricks: backtick-quoted identifiers and <c>:name</c> parameter
/// markers (matching <c>GlutenFree.Databricks.AdoNet</c>).
/// </summary>
public class DatabricksSqlGenerationHelper(RelationalSqlGenerationHelperDependencies dependencies)
    : RelationalSqlGenerationHelper(dependencies)
{
    private const char IdentifierQuote = '`';

    /// <inheritdoc />
    /// <remarks>
    /// Databricks uses <c>:name</c> markers, not <c>@name</c>; the ADO.NET provider parses the
    /// same form.
    /// </remarks>
    public override string GenerateParameterName(string name)
        => ":" + name;

    /// <inheritdoc />
    public override void GenerateParameterName(StringBuilder builder, string name)
        => builder.Append(':').Append(name);

    /// <inheritdoc />
    /// <remarks>
    /// The parameter name used in <see cref="System.Data.Common.DbParameter.ParameterName" />
    /// carries no marker: the ADO.NET provider matches parameters by bare name.
    /// </remarks>
    public override string GenerateParameterNamePlaceholder(string name)
        => GenerateParameterName(name);

    /// <inheritdoc />
    public override void GenerateParameterNamePlaceholder(StringBuilder builder, string name)
        => GenerateParameterName(builder, name);

    /// <inheritdoc />
    public override string EscapeIdentifier(string identifier)
        => identifier.Replace("`", "``", StringComparison.Ordinal);

    /// <inheritdoc />
    public override void EscapeIdentifier(StringBuilder builder, string identifier)
    {
        var start = builder.Length;
        builder.Append(identifier);
        builder.Replace("`", "``", start, identifier.Length);
    }

    /// <inheritdoc />
    public override string DelimitIdentifier(string identifier)
        => $"{IdentifierQuote}{EscapeIdentifier(identifier)}{IdentifierQuote}";

    /// <inheritdoc />
    public override void DelimitIdentifier(StringBuilder builder, string identifier)
    {
        builder.Append(IdentifierQuote);
        EscapeIdentifier(builder, identifier);
        builder.Append(IdentifierQuote);
    }
}
