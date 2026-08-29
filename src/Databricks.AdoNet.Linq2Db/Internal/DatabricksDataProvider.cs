using System.Data.Common;
using LinqToDB;
using LinqToDB.DataProvider;
using LinqToDB.Internal.DataProvider;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Mapping;
using LinqToDB.SchemaProvider;

namespace Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// linq2db data provider for Databricks SQL warehouses, backed by Databricks.AdoNet.
/// </summary>
public sealed class DatabricksDataProvider : DataProviderBase
{
    private readonly DatabricksSqlOptimizer _sqlOptimizer;

    /// <summary>Creates the provider.</summary>
    public DatabricksDataProvider()
        : base(DatabricksProviderName.Databricks, DatabricksMappingSchema.Instance)
    {
        SqlProviderFlags.IsCommonTableExpressionsSupported = true;
        SqlProviderFlags.IsSubQueryOrderBySupported = true;
        SqlProviderFlags.IsInsertOrUpdateSupported = false; // Use MERGE explicitly instead.
        SqlProviderFlags.IsUpdateFromSupported = false;
        SqlProviderFlags.IsNullsOrderingSupported = true;
        SqlProviderFlags.IsWindowFunctionsSupported = true;
        SqlProviderFlags.IsAllSetOperationsSupported = true;

        _sqlOptimizer = new DatabricksSqlOptimizer(SqlProviderFlags);
    }

    /// <inheritdoc />
    public override string? ConnectionNamespace => typeof(DatabricksConnection).Namespace;

    /// <inheritdoc />
    public override Type DataReaderType => typeof(DatabricksDataReader);

    /// <inheritdoc />
    /// <remarks>Databricks SQL has no multi-statement transactions.</remarks>
    public override bool TransactionsSupported => false;

    /// <inheritdoc />
    public override TableOptions SupportedTableOptions =>
        TableOptions.CreateIfNotExists | TableOptions.DropIfExists;

    /// <inheritdoc />
    protected override DbConnection CreateConnectionInternal(string connectionString)
        => new DatabricksConnection(connectionString);

    /// <inheritdoc />
    public override ISqlBuilder CreateSqlBuilder(MappingSchema mappingSchema, DataOptions dataOptions)
        => new DatabricksSqlBuilder(this, mappingSchema, dataOptions, GetSqlOptimizer(dataOptions), SqlProviderFlags);

    /// <inheritdoc />
    public override ISqlOptimizer GetSqlOptimizer(DataOptions dataOptions) => _sqlOptimizer;

    /// <inheritdoc />
    public override ISchemaProvider GetSchemaProvider() => new DatabricksSchemaProvider();

    /// <inheritdoc />
    protected override LinqToDB.Linq.Translation.IMemberTranslator CreateMemberTranslator()
        => new DatabricksMemberTranslator();
}
