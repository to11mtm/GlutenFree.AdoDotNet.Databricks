using System.Data;
using System.Data.Common;
using System.Globalization;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// A named input parameter for a <see cref="DatabricksCommand"/>. Parameters are sent to the
/// Statement Execution API as native typed parameters (server-side binding), never via
/// client-side string substitution.
/// </summary>
public sealed class DatabricksParameter : DbParameter
{
    private string _parameterName = string.Empty;
    private ParameterDirection _direction = ParameterDirection.Input;

    /// <summary>Creates an unnamed parameter.</summary>
    public DatabricksParameter()
    {
    }

    /// <summary>Creates a named parameter with a value.</summary>
    public DatabricksParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    /// <inheritdoc />
    public override DbType DbType { get; set; } = DbType.Object;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">For any direction other than Input.</exception>
    public override ParameterDirection Direction
    {
        get => _direction;
        set => _direction = value == ParameterDirection.Input
            ? value
            : throw new NotSupportedException("Databricks statements support input parameters only.");
    }

    /// <inheritdoc />
    public override bool IsNullable { get; set; } = true;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = (value ?? string.Empty).TrimStart(':');
    }

    /// <inheritdoc />
    public override int Size { get; set; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string SourceColumn
    {
        get => string.Empty;
        set { }
    }

    /// <inheritdoc />
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc />
    public override object? Value { get; set; }

    /// <inheritdoc />
    public override void ResetDbType() => DbType = DbType.Object;

    /// <summary>
    /// Converts this parameter to the wire representation: an invariant string value and a
    /// Databricks SQL type name inferred from the .NET value (or <see cref="DbType"/> override).
    /// </summary>
    internal Transport.StatementParameter ToStatementParameter()
    {
        if (_parameterName.Length == 0)
        {
            throw new InvalidOperationException("Databricks parameters must have a name (used as the :name marker).");
        }

        var (text, typeName) = FormatValue();
        return new Transport.StatementParameter
        {
            Name = _parameterName,
            Value = text,
            Type = typeName,
        };
    }

    private (string? Text, string? TypeName) FormatValue()
    {
        var value = Value;
        if (value is null || value is DBNull)
        {
            // A null value with an explicit type lets the server bind a typed NULL.
            return (null, DbTypeToDatabricksType());
        }

        return value switch
        {
            bool b => (b ? "TRUE" : "FALSE", "BOOLEAN"),
            sbyte i => (i.ToString(CultureInfo.InvariantCulture), "TINYINT"),
            byte i => (i.ToString(CultureInfo.InvariantCulture), "SMALLINT"),
            short i => (i.ToString(CultureInfo.InvariantCulture), "SMALLINT"),
            ushort i => (i.ToString(CultureInfo.InvariantCulture), "INT"),
            int i => (i.ToString(CultureInfo.InvariantCulture), "INT"),
            uint i => (i.ToString(CultureInfo.InvariantCulture), "BIGINT"),
            long i => (i.ToString(CultureInfo.InvariantCulture), "BIGINT"),
            float f => (f.ToString("R", CultureInfo.InvariantCulture), "FLOAT"),
            double d => (d.ToString("R", CultureInfo.InvariantCulture), "DOUBLE"),
            decimal m => FormatDecimal(m),
            string s => (s, "STRING"),
            char c => (c.ToString(), "STRING"),
            Guid g => (g.ToString("D"), "STRING"),
            DateOnly d => (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "DATE"),
            DateTime dt => (dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                dt.Kind == DateTimeKind.Unspecified ? "TIMESTAMP_NTZ" : "TIMESTAMP"),
            DateTimeOffset dto => (dto.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture), "TIMESTAMP"),
            System.Data.SqlTypes.SqlDecimal sd => (sd.ToString(), $"DECIMAL({sd.Precision},{sd.Scale})"),
            byte[] => throw new NotSupportedException(
                "BINARY parameters are not supported by the Databricks Statement Execution API. " +
                "Consider passing a hex/base64 STRING and decoding in SQL."),
            _ => throw new NotSupportedException(
                $"Values of type '{value.GetType()}' are not supported as Databricks parameters."),
        };
    }

    private static (string, string) FormatDecimal(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var scale = value.Scale;
        var precision = text.Count(char.IsAsciiDigit);
        // Precision must at least cover the digits present.
        return (text, $"DECIMAL({Math.Max(precision, 1)},{scale})");
    }

    private string? DbTypeToDatabricksType() => DbType switch
    {
        DbType.Boolean => "BOOLEAN",
        DbType.SByte => "TINYINT",
        DbType.Byte or DbType.Int16 => "SMALLINT",
        DbType.UInt16 or DbType.Int32 => "INT",
        DbType.UInt32 or DbType.Int64 or DbType.UInt64 => "BIGINT",
        DbType.Single => "FLOAT",
        DbType.Double => "DOUBLE",
        DbType.Decimal or DbType.Currency or DbType.VarNumeric => "DECIMAL(38,18)",
        DbType.Date => "DATE",
        DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset or DbType.Time => "TIMESTAMP",
        DbType.String or DbType.AnsiString or DbType.StringFixedLength
            or DbType.AnsiStringFixedLength or DbType.Guid or DbType.Xml => "STRING",
        _ => null,
    };
}
