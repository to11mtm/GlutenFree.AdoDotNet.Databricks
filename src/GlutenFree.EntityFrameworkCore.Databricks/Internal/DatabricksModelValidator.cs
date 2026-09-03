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
        ValidateNoStoreGeneratedValues(model);
    }

    /// <summary>
    /// Rejects properties EF would expect the store to populate. Databricks has no
    /// <c>RETURNING</c>/<c>OUTPUT</c> clause, so a store-generated value can never be read back
    /// into the tracked entity, and a concurrency token cannot be checked because the provider
    /// batches statements into a <c>BEGIN ATOMIC</c> block that reports no per-statement rows
    /// affected. Both fail silently if left to run, so they are refused at model-validation time
    /// with a message naming the alternative.
    /// </summary>
    private static void ValidateNoStoreGeneratedValues(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.ValueGenerated != ValueGenerated.Never)
                {
                    throw new InvalidOperationException(
                        $"Property '{entityType.DisplayName()}.{property.Name}' is configured as "
                        + $"store-generated ('{property.ValueGenerated}'), which Databricks cannot support: "
                        + "there is no RETURNING/OUTPUT clause to read the generated value back after an "
                        + "insert or update. Generate the value on the client instead — call "
                        + "'ValueGeneratedNever()' and assign it yourself, or use a client-side value "
                        + "generator such as a GUID.");
                }

                if (property.IsConcurrencyToken)
                {
                    throw new InvalidOperationException(
                        $"Property '{entityType.DisplayName()}.{property.Name}' is configured as a "
                        + "concurrency token, which this provider does not support yet. Optimistic "
                        + "concurrency relies on the number of rows affected by each statement, and "
                        + "Databricks does not report that per statement inside the atomic block the "
                        + "provider uses to make SaveChanges all-or-nothing. Remove "
                        + "'IsConcurrencyToken()' and handle conflicts in the application, or rely on "
                        + "Delta's own optimistic concurrency, which fails the whole transaction on a "
                        + "conflicting write.");
                }
            }
        }
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
