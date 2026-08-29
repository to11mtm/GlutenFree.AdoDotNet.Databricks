using System.Text;
using LinqToDB;
using LinqToDB.DataProvider;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;
using LinqToDB.SqlQuery;

namespace Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// SQL builder for Databricks SQL: backtick identifier quoting, <c>:name</c> parameter
/// markers (matching Databricks.AdoNet), LIMIT/OFFSET pagination, and Databricks type names
/// for DDL.
/// </summary>
public class DatabricksSqlBuilder : BasicSqlBuilder
{
    private const char IdentifierQuote = '`';

    /// <summary>Creates a builder for the given provider and options.</summary>
    public DatabricksSqlBuilder(
        IDataProvider? provider,
        MappingSchema mappingSchema,
        DataOptions dataOptions,
        ISqlOptimizer sqlOptimizer,
        SqlProviderFlags sqlProviderFlags)
        : base(provider, mappingSchema, dataOptions, sqlOptimizer, sqlProviderFlags)
    {
    }

    DatabricksSqlBuilder(BasicSqlBuilder parentBuilder)
        : base(parentBuilder)
    {
    }

    /// <inheritdoc />
    protected override ISqlBuilder CreateSqlBuilder() => new DatabricksSqlBuilder(this);

    /// <inheritdoc />
    protected override string LimitFormat(SelectQuery selectQuery) => "LIMIT {0}";

    /// <inheritdoc />
    protected override string OffsetFormat(SelectQuery selectQuery) => "OFFSET {0}";

    /// <inheritdoc />
    public override StringBuilder Convert(StringBuilder sb, string value, ConvertType convertType)
    {
        switch (convertType)
        {
            case ConvertType.NameToQueryParameter:
                // Databricks.AdoNet uses :name markers bound server-side.
                return sb.Append(':').Append(value);

            case ConvertType.NameToCommandParameter:
            case ConvertType.NameToSprocParameter:
            case ConvertType.SprocParameterToName:
                return sb.Append(value);

            case ConvertType.NameToQueryField:
            case ConvertType.NameToQueryFieldAlias:
            case ConvertType.NameToQueryTable:
            case ConvertType.NameToQueryTableAlias:
            case ConvertType.NameToDatabase:
            case ConvertType.NameToSchema:
            case ConvertType.NameToProcedure:
            case ConvertType.NameToCteName:
                EscapeIdentifier(sb, value);
                return sb;

            default:
                return sb.Append(value);
        }
    }

    private static void EscapeIdentifier(StringBuilder sb, string value)
    {
        sb.Append(IdentifierQuote);
        foreach (var c in value)
        {
            sb.Append(c);
            if (c == IdentifierQuote)
            {
                sb.Append(IdentifierQuote);
            }
        }

        sb.Append(IdentifierQuote);
    }

    /// <inheritdoc />
    protected override void BuildDataTypeFromDataType(DbDataType type, bool forCreateTable, bool canBeNull)
    {
        switch (type.DataType)
        {
            case DataType.Boolean:
                StringBuilder.Append("BOOLEAN");
                return;
            case DataType.SByte:
                StringBuilder.Append("TINYINT");
                return;
            case DataType.Byte:
            case DataType.Int16:
                StringBuilder.Append("SMALLINT");
                return;
            case DataType.UInt16:
            case DataType.Int32:
                StringBuilder.Append("INT");
                return;
            case DataType.UInt32:
            case DataType.Int64:
            case DataType.UInt64:
                StringBuilder.Append("BIGINT");
                return;
            case DataType.Single:
                StringBuilder.Append("FLOAT");
                return;
            case DataType.Double:
                StringBuilder.Append("DOUBLE");
                return;
            case DataType.Decimal:
            case DataType.Money:
            case DataType.SmallMoney:
            case DataType.VarNumeric:
                StringBuilder
                    .Append("DECIMAL(")
                    .Append(type.Precision is > 0 ? type.Precision.Value : 38)
                    .Append(',')
                    .Append(type.Scale ?? 6)
                    .Append(')');
                return;
            case DataType.Date:
                StringBuilder.Append("DATE");
                return;
            case DataType.DateTime:
            case DataType.DateTime2:
            case DataType.DateTimeOffset:
            case DataType.Timestamp:
                StringBuilder.Append("TIMESTAMP");
                return;
            case DataType.Binary:
            case DataType.VarBinary:
            case DataType.Blob:
                StringBuilder.Append("BINARY");
                return;
            case DataType.Char:
            case DataType.NChar:
            case DataType.VarChar:
            case DataType.NVarChar:
            case DataType.Text:
            case DataType.NText:
            case DataType.Guid:
            case DataType.Json:
                StringBuilder.Append("STRING");
                return;
            default:
                base.BuildDataTypeFromDataType(type, forCreateTable, canBeNull);
                return;
        }
    }

    /// <inheritdoc />
    public override StringBuilder BuildObjectName(
        StringBuilder sb,
        SqlObjectName name,
        ConvertType objectType = ConvertType.NameToQueryTable,
        bool escape = true,
        TableOptions tableOptions = TableOptions.NotSet,
        bool withoutSuffix = false)
    {
        // Databricks three-part names: catalog.schema.table (Server slot carries the catalog).
        if (name.Server is not null)
        {
            (escape ? Convert(sb, name.Server, ConvertType.NameToDatabase) : sb.Append(name.Server)).Append('.');
        }

        if (name.Schema is not null)
        {
            (escape ? Convert(sb, name.Schema, ConvertType.NameToSchema) : sb.Append(name.Schema)).Append('.');
        }

        return escape ? Convert(sb, name.Name, objectType) : sb.Append(name.Name);
    }
}
