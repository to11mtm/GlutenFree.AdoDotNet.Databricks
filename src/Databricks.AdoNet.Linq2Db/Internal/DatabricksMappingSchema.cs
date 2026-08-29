using System.Globalization;
using System.Text;
using LinqToDB;
using LinqToDB.Internal.Mapping;

namespace Databricks.AdoNet.Linq2Db.Internal;

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

        SetValueToSqlConverter(typeof(string), (sb, _, _, v) => BuildStringLiteral(sb, (string)v));
        SetValueToSqlConverter(typeof(char), (sb, _, _, v) => BuildStringLiteral(sb, ((char)v).ToString()));
        SetValueToSqlConverter(typeof(Guid), (sb, _, _, v) => sb.Append('\'').Append((Guid)v).Append('\''));
        SetValueToSqlConverter(typeof(bool), (sb, _, _, v) => sb.Append((bool)v ? "TRUE" : "FALSE"));
        SetValueToSqlConverter(typeof(DateTime), (sb, _, _, v) => sb.AppendFormat(
            CultureInfo.InvariantCulture, "TIMESTAMP '{0:yyyy-MM-dd HH:mm:ss.ffffff}'", (DateTime)v));
        SetValueToSqlConverter(typeof(DateTimeOffset), (sb, _, _, v) => sb.AppendFormat(
            CultureInfo.InvariantCulture, "TIMESTAMP '{0:yyyy-MM-dd HH:mm:ss.ffffffzzz}'", (DateTimeOffset)v));
        SetValueToSqlConverter(typeof(DateOnly), (sb, _, _, v) => sb.AppendFormat(
            CultureInfo.InvariantCulture, "DATE '{0:yyyy-MM-dd}'", (DateOnly)v));
        SetValueToSqlConverter(typeof(byte[]), (sb, _, _, v) => BuildBinaryLiteral(sb, (byte[])v));

        SetConvertExpression<string, Guid>(s => Guid.Parse(s));
    }

    private static void BuildStringLiteral(StringBuilder sb, string value)
    {
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\'':
                    sb.Append("''");
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
        sb.Append("X'");
        foreach (var b in value)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        sb.Append('\'');
    }
}
