using System.Data;
using GlutenFree.Databricks.AdoNet;
using LinqToDB.Data;
using LinqToDB.SchemaProvider;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// Minimal linq2db schema provider for Databricks, backed by
/// <see cref="DatabricksConnection.GetSchema(string, string?[])"/> (Unity Catalog
/// <c>information_schema</c>). Provides tables and columns; keys/procedures are not
/// applicable to Databricks SQL.
/// </summary>
public sealed class DatabricksSchemaProvider : ISchemaProvider
{
    /// <inheritdoc />
    public DatabaseSchema GetSchema(DataConnection dataConnection, GetSchemaOptions? options = null)
    {
        if (dataConnection.OpenDbConnection() is not DatabricksConnection connection)
        {
            throw new InvalidOperationException("DatabricksSchemaProvider requires a DatabricksConnection.");
        }

        var catalog = connection.Catalog is { Length: > 0 } c ? c : null;
        var schema = connection.Database is { Length: > 0 } s ? s : null;

        var tablesData = connection.GetSchema("Tables", [catalog, schema, null]);
        var viewsData = connection.GetSchema("Views", [catalog, schema, null]);
        var columnsData = connection.GetSchema("Columns", [catalog, schema, null, null]);

        var views = new HashSet<(string, string, string)>(
            viewsData.Rows.Cast<DataRow>().Select(r =>
                ((string)r["TABLE_CATALOG"], (string)r["TABLE_SCHEMA"], (string)r["TABLE_NAME"])));

        var columnsByTable = columnsData.Rows.Cast<DataRow>()
            .GroupBy(r => ((string)r["TABLE_CATALOG"], (string)r["TABLE_SCHEMA"], (string)r["TABLE_NAME"]))
            .ToDictionary(g => g.Key, g => g.ToList());

        var tables = new List<TableSchema>();
        foreach (DataRow row in tablesData.Rows)
        {
            var key = ((string)row["TABLE_CATALOG"], (string)row["TABLE_SCHEMA"], (string)row["TABLE_NAME"]);
            var tableSchema = new TableSchema
            {
                CatalogName = key.Item1,
                SchemaName = key.Item2,
                TableName = key.Item3,
                TypeName = ToPascalCase(key.Item3),
                IsView = views.Contains(key),
                IsDefaultSchema = string.Equals(key.Item2, schema ?? "default", StringComparison.OrdinalIgnoreCase),
                Columns = [],
                ForeignKeys = [],
            };

            if (columnsByTable.TryGetValue(key, out var tableColumns))
            {
                foreach (var columnRow in tableColumns.OrderBy(r => Convert.ToInt32(r["ORDINAL_POSITION"])))
                {
                    var dataType = (string)columnRow["DATA_TYPE"];
                    var systemType = GetSystemType(dataType);
                    tableSchema.Columns.Add(new ColumnSchema
                    {
                        ColumnName = (string)columnRow["COLUMN_NAME"],
                        MemberName = ToPascalCase((string)columnRow["COLUMN_NAME"]),
                        ColumnType = columnRow["FULL_DATA_TYPE"] as string ?? dataType,
                        IsNullable = string.Equals(
                            columnRow["IS_NULLABLE"] as string, "YES", StringComparison.OrdinalIgnoreCase),
                        SystemType = systemType,
                        MemberType = systemType.Name,
                        DataType = GetLinqToDbDataType(dataType),
                    });
                }
            }

            tables.Add(tableSchema);
        }

        return new DatabaseSchema
        {
            DataSource = connection.DataSource,
            Database = catalog ?? string.Empty,
            ServerVersion = connection.ServerVersion,
            Tables = tables,
            Procedures = [],
            ProviderSpecificTypeNamespace = typeof(DatabricksConnection).Namespace,
        };
    }

    private static string ToPascalCase(string name)
    {
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? name
            : string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static Type GetSystemType(string dataType) => dataType.ToUpperInvariant() switch
    {
        "BOOLEAN" => typeof(bool),
        "TINYINT" or "BYTE" => typeof(sbyte),
        "SMALLINT" or "SHORT" => typeof(short),
        "INT" or "INTEGER" => typeof(int),
        "BIGINT" or "LONG" => typeof(long),
        "FLOAT" or "REAL" => typeof(float),
        "DOUBLE" => typeof(double),
        "DECIMAL" => typeof(decimal),
        "DATE" => typeof(DateOnly),
        "TIMESTAMP" or "TIMESTAMP_NTZ" => typeof(DateTime),
        "BINARY" => typeof(byte[]),
        _ => typeof(string),
    };

    private static LinqToDB.DataType GetLinqToDbDataType(string dataType) => dataType.ToUpperInvariant() switch
    {
        "BOOLEAN" => LinqToDB.DataType.Boolean,
        "TINYINT" or "BYTE" => LinqToDB.DataType.SByte,
        "SMALLINT" or "SHORT" => LinqToDB.DataType.Int16,
        "INT" or "INTEGER" => LinqToDB.DataType.Int32,
        "BIGINT" or "LONG" => LinqToDB.DataType.Int64,
        "FLOAT" or "REAL" => LinqToDB.DataType.Single,
        "DOUBLE" => LinqToDB.DataType.Double,
        "DECIMAL" => LinqToDB.DataType.Decimal,
        "DATE" => LinqToDB.DataType.Date,
        "TIMESTAMP" or "TIMESTAMP_NTZ" => LinqToDB.DataType.DateTime,
        "BINARY" => LinqToDB.DataType.VarBinary,
        _ => LinqToDB.DataType.NVarChar,
    };
}
