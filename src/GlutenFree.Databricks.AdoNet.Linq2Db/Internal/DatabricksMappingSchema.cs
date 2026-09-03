using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using LinqToDB;
using LinqToDB.Internal.Mapping;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// linq2db mapping schema for Databricks SQL: literal rendering and default type mappings.
/// </summary>
public sealed class DatabricksMappingSchema : LockedMappingSchema
{
    /// <summary>Shared instance.</summary>
    public static readonly DatabricksMappingSchema Instance = new();

    private DatabricksMappingSchema()
        : base(DatabricksProviderName.Databricks)
    {
        SetDataType(typeof(string), DataType.NVarChar);
        SetDataType(typeof(Guid), DataType.NVarChar);
        SetDataType(typeof(DatabricksDecimal), DataType.Decimal);
        SetDataType(typeof(DatabricksDecimal?), DataType.Decimal);

        SetValueToSqlConverter(typeof(string), (sb, _, _, v) => BuildStringLiteral(sb, (string)v));
        SetValueToSqlConverter(typeof(char), (sb, _, _, v) => BuildStringLiteral(sb, ((char)v).ToString()));
        SetValueToSqlConverter(typeof(Guid), (sb, _, _, v) => sb.Append('\'').Append((Guid)v).Append('\''));
        SetValueToSqlConverter(typeof(bool), (sb, _, _, v) => sb.Append((bool)v ? "TRUE" : "FALSE"));
        SetValueToSqlConverter(
            typeof(DatabricksDecimal), (sb, _, _, v) => sb.Append(((DatabricksDecimal)v).ToString()));
        SetValueToSqlConverter(typeof(DateTime), (sb, _, _, v) => sb.AppendFormat(
            CultureInfo.InvariantCulture, "TIMESTAMP '{0:yyyy-MM-dd HH:mm:ss.ffffff}'", (DateTime)v));
        SetValueToSqlConverter(typeof(DateTimeOffset), (sb, _, _, v) => sb.AppendFormat(
            CultureInfo.InvariantCulture, "TIMESTAMP '{0:yyyy-MM-dd HH:mm:ss.ffffffzzz}'", (DateTimeOffset)v));
        SetValueToSqlConverter(typeof(DateOnly), (sb, _, _, v) => sb.AppendFormat(
            CultureInfo.InvariantCulture, "DATE '{0:yyyy-MM-dd}'", (DateOnly)v));
        SetValueToSqlConverter(typeof(byte[]), (sb, _, _, v) => BuildBinaryLiteral(sb, (byte[])v));

        SetConvertExpression<string, Guid>(s => Guid.Parse(s));

        // A DECIMAL(29..38, s) column holds more precision than a .NET decimal, so the data
        // reader hands those values back as SqlDecimal (or decimal for narrow ones). Without
        // these, reading such a column into a DatabricksDecimal property fails with
        // LinqToDBConvertException and reading it into a decimal overflows. Round-tripping
        // through the canonical string keeps every digit.
        SetConvertExpression<SqlDecimal, DatabricksDecimal>(v => DatabricksDecimal.FromSqlDecimal(v));
        SetConvertExpression<decimal, DatabricksDecimal>(v => DatabricksDecimal.FromDecimal(v));
        SetConvertExpression<string, DatabricksDecimal>(v => DatabricksDecimal.Parse(v));
        SetConvertExpression<DatabricksDecimal, SqlDecimal>(v => v.ToSqlDecimal());
        SetConvertExpression<DatabricksDecimal, decimal>(v => v.ToDecimal());
        SetConvertExpression<DatabricksDecimal, string>(v => v.ToString());

        SetConvertExpression<SqlDecimal, DatabricksDecimal?>(v => DatabricksDecimal.FromSqlDecimal(v));
        SetConvertExpression<decimal, DatabricksDecimal?>(v => DatabricksDecimal.FromDecimal(v));
    }

    private static void BuildStringLiteral(StringBuilder sb, string value)
    {
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                // Spark SQL uses backslash escaping; doubling quotes ('') is parsed as
                // adjacent-literal concatenation and silently drops the quote.
                case '\'':
                    sb.Append(@"\'");
                    break;
                case '\\':
                    sb.Append(@"\\");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('\'');
    }

    private static void BuildBinaryLiteral(StringBuilder sb, byte[] value)
    {
        sb.Append("X'")
            .AppendByteArrayAsHexViaLookup32(value)
            .Append('\'');
    }
}
