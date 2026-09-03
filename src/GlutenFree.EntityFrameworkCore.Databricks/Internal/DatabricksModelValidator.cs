using System.Globalization;
using System.Text.RegularExpressions;
using GlutenFree.Databricks.AdoNet;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace GlutenFree.EntityFrameworkCore.Databricks.Internal;

/// <summary>
/// Adds Databricks-specific model checks on top of the relational validator.
/// </summary>
public partial class DatabricksModelValidator(
    ModelValidatorDependencies dependencies,
    RelationalModelValidatorDependencies relationalDependencies)
    : RelationalModelValidator(dependencies, relationalDependencies)
{
    /// <summary>
    /// The largest <c>DECIMAL</c> precision that always fits in a .NET <see cref="decimal" />.
    /// </summary>
    private const int MaxSafeDecimalPrecision = 28;

    /// <inheritdoc />
    public override void Validate(IModel model, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        base.Validate(model, logger);

        ValidateDecimalPrecision(model, logger);
    }

    /// <summary>
    /// Warns when a <see cref="decimal" /> property is mapped to a <c>DECIMAL</c> column whose
    /// precision exceeds what <see cref="decimal" /> can represent. Such a column overflows on
    /// read, and only for the rows that actually use the extra digits — so it fails in
    /// production rather than in testing.
    /// </summary>
    private static void ValidateDecimalPrecision(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType != typeof(decimal))
                {
                    continue;
                }

                var precision = GetDeclaredPrecision(property);
                if (precision > MaxSafeDecimalPrecision)
                {
                    logger.Logger.LogWarning(
                        "Property '{EntityType}.{Property}' is a 'decimal' mapped to a column with "
                        + "precision {Precision}. .NET's decimal holds about {MaxPrecision} significant "
                        + "digits, so values using the extra precision will overflow when read. Use "
                        + "'{BigDecimal}' for a lossless mapping, or reduce the column precision.",
                        entityType.DisplayName(),
                        property.Name,
                        precision,
                        MaxSafeDecimalPrecision,
                        nameof(DatabricksDecimal));
                }
            }
        }
    }

    /// <summary>
    /// The precision declared for a property, whether it came from the fluent API or from an
    /// explicit <c>DECIMAL(p, s)</c> column type.
    /// </summary>
    private static int? GetDeclaredPrecision(IProperty property)
    {
        if (property.GetPrecision() is { } precision)
        {
            return precision;
        }

        var columnType = property.GetColumnType();
        if (columnType is null)
        {
            return null;
        }

        var match = DecimalStoreType().Match(columnType);
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }

    [GeneratedRegex(@"^\s*(?:DECIMAL|DEC|NUMERIC)\s*\(\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalStoreType();
}
