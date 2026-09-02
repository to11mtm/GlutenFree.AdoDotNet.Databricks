using GlutenFree.Databricks.AdoNet;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// Creates, drops and probes the schema an <see cref="Microsoft.EntityFrameworkCore.DbContext" />
/// maps to.
/// </summary>
/// <remarks>
/// A Databricks "database" in EF terms is a Unity Catalog schema: the catalog is provisioned out
/// of band (by an administrator), so <see cref="Create" /> issues <c>CREATE SCHEMA</c> and
/// <see cref="Delete" /> issues <c>DROP SCHEMA ... CASCADE</c> rather than touching the catalog.
/// </remarks>
public class DatabricksDatabaseCreator(
    RelationalDatabaseCreatorDependencies dependencies,
    IDatabricksRelationalConnection connection,
    IRawSqlCommandBuilder rawSqlCommandBuilder)
    : RelationalDatabaseCreator(dependencies)
{
    /// <inheritdoc />
    public override void Create()
        => ExecuteNonQuery($"CREATE SCHEMA IF NOT EXISTS {QualifiedSchema()}");

    /// <inheritdoc />
    public override async Task CreateAsync(CancellationToken cancellationToken = default)
        => await ExecuteNonQueryAsync($"CREATE SCHEMA IF NOT EXISTS {QualifiedSchema()}", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public override void Delete()
        => ExecuteNonQuery($"DROP SCHEMA IF EXISTS {QualifiedSchema()} CASCADE");

    /// <inheritdoc />
    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        => await ExecuteNonQueryAsync($"DROP SCHEMA IF EXISTS {QualifiedSchema()} CASCADE", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>True when the mapped schema is visible in <c>information_schema</c>.</remarks>
    public override bool Exists()
        => ExecuteScalarLong(SchemaExistsSql()) > 0;

    /// <inheritdoc />
    public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => await ExecuteScalarLongAsync(SchemaExistsSql(), cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc />
    public override bool HasTables()
        => ExecuteScalarLong(HasTablesSql()) > 0;

    /// <inheritdoc />
    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
        => await ExecuteScalarLongAsync(HasTablesSql(), cancellationToken).ConfigureAwait(false) > 0;

    /// <summary>The catalog-qualified schema this context maps to, quoted for SQL.</summary>
    private string QualifiedSchema()
    {
        var databricksConnection = (DatabricksConnection)connection.DbConnection;
        var catalog = databricksConnection.Catalog;
        var schema = databricksConnection.Database;

        if (string.IsNullOrEmpty(schema))
        {
            throw new InvalidOperationException(
                "No schema is configured. Set 'Schema' in the connection string or call "
                + "UseSchema(...) so the provider knows which Unity Catalog schema the context maps to.");
        }

        return string.IsNullOrEmpty(catalog)
            ? Quote(schema)
            : $"{Quote(catalog)}.{Quote(schema)}";
    }

    private string SchemaExistsSql()
    {
        var databricksConnection = (DatabricksConnection)connection.DbConnection;
        return
            $"SELECT COUNT(*) FROM {InformationSchema()}.schemata "
            + $"WHERE schema_name = {Literal(databricksConnection.Database)}";
    }

    private string HasTablesSql()
    {
        var databricksConnection = (DatabricksConnection)connection.DbConnection;
        return
            $"SELECT COUNT(*) FROM {InformationSchema()}.tables "
            + $"WHERE table_schema = {Literal(databricksConnection.Database)}";
    }

    /// <summary>
    /// The <c>information_schema</c> to query. It is per-catalog, so it must be qualified with
    /// the configured catalog when there is one.
    /// </summary>
    private string InformationSchema()
    {
        var catalog = ((DatabricksConnection)connection.DbConnection).Catalog;
        return string.IsNullOrEmpty(catalog) ? "information_schema" : $"{Quote(catalog)}.information_schema";
    }

    private static string Quote(string identifier)
        => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    /// <summary>
    /// Renders a string literal. Spark SQL uses backslash escaping — doubling the quote is
    /// parsed as adjacent-literal concatenation and silently drops the quote.
    /// </summary>
    private static string Literal(string value)
        => "'" + value.Replace("\\", @"\\", StringComparison.Ordinal).Replace("'", @"\'", StringComparison.Ordinal) + "'";

    private RelationalCommandParameterObject CreateParameterObject()
        => new(
            Dependencies.Connection,
            parameterValues: null,
            readerColumns: null,
            Dependencies.CurrentContext.Context,
            Dependencies.CommandLogger,
            CommandSource.Migrations);

    private void ExecuteNonQuery(string sql)
        => rawSqlCommandBuilder.Build(sql).ExecuteNonQuery(CreateParameterObject());

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
        => await rawSqlCommandBuilder.Build(sql)
            .ExecuteNonQueryAsync(CreateParameterObject(), cancellationToken)
            .ConfigureAwait(false);

    private long ExecuteScalarLong(string sql)
        => Convert.ToInt64(rawSqlCommandBuilder.Build(sql).ExecuteScalar(CreateParameterObject()));

    private async Task<long> ExecuteScalarLongAsync(string sql, CancellationToken cancellationToken)
        => Convert.ToInt64(
            await rawSqlCommandBuilder.Build(sql)
                .ExecuteScalarAsync(CreateParameterObject(), cancellationToken)
                .ConfigureAwait(false));
}
