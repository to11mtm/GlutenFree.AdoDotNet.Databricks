using System.Data;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// Implements <see cref="DatabricksConnection.GetSchema(string, string?[])"/> collections
/// by querying Unity Catalog's <c>system.information_schema</c> views.
/// </summary>
internal static class DatabricksSchemaProvider
{
    internal const string MetaDataCollections = "MetaDataCollections";
    internal const string Catalogs = "Catalogs";
    internal const string Schemas = "Schemas";
    internal const string Tables = "Tables";
    internal const string Views = "Views";
    internal const string Columns = "Columns";

    internal static DataTable GetSchema(
        DatabricksConnection connection, string collectionName, string?[]? restrictions)
    {
        restrictions ??= [];
        return collectionName.ToUpperInvariant() switch
        {
            "METADATACOLLECTIONS" => BuildMetaDataCollections(),
            "CATALOGS" => Query(
                connection,
                Catalogs,
                "SELECT catalog_name AS CATALOG_NAME FROM system.information_schema.catalogs",
                [("catalog_name", Get(restrictions, 0))],
                orderBy: "CATALOG_NAME"),
            "SCHEMAS" => Query(
                connection,
                Schemas,
                "SELECT catalog_name AS CATALOG_NAME, schema_name AS SCHEMA_NAME FROM system.information_schema.schemata",
                [("catalog_name", Get(restrictions, 0)), ("schema_name", Get(restrictions, 1))],
                orderBy: "CATALOG_NAME, SCHEMA_NAME"),
            "TABLES" => Query(
                connection,
                Tables,
                "SELECT table_catalog AS TABLE_CATALOG, table_schema AS TABLE_SCHEMA, table_name AS TABLE_NAME, " +
                "table_type AS TABLE_TYPE FROM system.information_schema.tables",
                [
                    ("table_catalog", Get(restrictions, 0)),
                    ("table_schema", Get(restrictions, 1)),
                    ("table_name", Get(restrictions, 2)),
                ],
                orderBy: "TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME"),
            "VIEWS" => Query(
                connection,
                Views,
                "SELECT table_catalog AS TABLE_CATALOG, table_schema AS TABLE_SCHEMA, table_name AS TABLE_NAME " +
                "FROM system.information_schema.views",
                [
                    ("table_catalog", Get(restrictions, 0)),
                    ("table_schema", Get(restrictions, 1)),
                    ("table_name", Get(restrictions, 2)),
                ],
                orderBy: "TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME"),
            "COLUMNS" => Query(
                connection,
                Columns,
                "SELECT table_catalog AS TABLE_CATALOG, table_schema AS TABLE_SCHEMA, table_name AS TABLE_NAME, " +
                "column_name AS COLUMN_NAME, ordinal_position AS ORDINAL_POSITION, data_type AS DATA_TYPE, " +
                "full_data_type AS FULL_DATA_TYPE, is_nullable AS IS_NULLABLE " +
                "FROM system.information_schema.columns",
                [
                    ("table_catalog", Get(restrictions, 0)),
                    ("table_schema", Get(restrictions, 1)),
                    ("table_name", Get(restrictions, 2)),
                    ("column_name", Get(restrictions, 3)),
                ],
                orderBy: "TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION"),
            _ => throw new ArgumentException(
                $"Schema collection '{collectionName}' is not supported. Supported collections: " +
                $"{MetaDataCollections}, {Catalogs}, {Schemas}, {Tables}, {Views}, {Columns}."),
        };
    }

    private static string? Get(string?[] restrictions, int index)
        => index < restrictions.Length && !string.IsNullOrEmpty(restrictions[index]) ? restrictions[index] : null;

    private static DataTable BuildMetaDataCollections()
    {
        var table = new DataTable(MetaDataCollections);
        table.Columns.Add("CollectionName", typeof(string));
        table.Columns.Add("NumberOfRestrictions", typeof(int));
        table.Rows.Add(MetaDataCollections, 0);
        table.Rows.Add(Catalogs, 1);
        table.Rows.Add(Schemas, 2);
        table.Rows.Add(Tables, 3);
        table.Rows.Add(Views, 3);
        table.Rows.Add(Columns, 4);
        return table;
    }

    private static DataTable Query(
        DatabricksConnection connection,
        string tableName,
        string baseQuery,
        (string Column, string? Value)[] filters,
        string orderBy)
    {
        using var command = connection.CreateCommand();
        var where = new List<string>();
        foreach (var (column, value) in filters)
        {
            if (value is not null)
            {
                // Restrictions are bound as native parameters — never interpolated into SQL.
                var parameterName = $"r_{column}";
                where.Add($"{column} = :{parameterName}");
                command.Parameters.AddWithValue(parameterName, value);
            }
        }

        command.CommandText = baseQuery
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
            + $" ORDER BY {orderBy}";

        using var reader = command.ExecuteReader();
        var table = new DataTable(tableName);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            table.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
        }

        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            table.Rows.Add(values);
        }

        return table;
    }
}
