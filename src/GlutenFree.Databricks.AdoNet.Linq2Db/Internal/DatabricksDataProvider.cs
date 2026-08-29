using System.Data.Common;
using GlutenFree.Databricks.AdoNet;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider;
using LinqToDB.Internal.DataProvider;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Mapping;
using LinqToDB.SchemaProvider;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// linq2db data provider for Databricks SQL warehouses, backed by GlutenFree.Databricks.AdoNet.
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
        SqlProviderFlags.IsInsertOrUpdateSupported = false; // No native upsert; linq2db lowers to MERGE.
        SqlProviderFlags.IsUpdateFromSupported = false; // Databricks UPDATE has no FROM clause.
        SqlProviderFlags.IsNullsOrderingSupported = true; // NULLS FIRST/LAST supported.
        SqlProviderFlags.IsWindowFunctionsSupported = true;
        SqlProviderFlags.IsAllSetOperationsSupported = true; // EXCEPT ALL / INTERSECT ALL.
        SqlProviderFlags.IsDistinctFromSupported = true; // IS [NOT] DISTINCT FROM.
        // CROSS/OUTER APPLY are emitted as INNER/LEFT JOIN LATERAL (see DatabricksSqlBuilder).
        SqlProviderFlags.IsApplyJoinSupported = true;
        SqlProviderFlags.IsCrossApplyJoinSupportsCondition = true;
        SqlProviderFlags.IsOuterApplyJoinSupportsCondition = true;
        // Row constructors: (a, b) = (1, 2) equality and (a, b) IN ((1, 2), ...) are supported.
        SqlProviderFlags.RowConstructorSupport = RowFeature.Equality | RowFeature.In;

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

    /// <inheritdoc />
    public override BulkCopyRowsCopied BulkCopy<T>(
        DataOptions options, ITable<T> table, IEnumerable<T> source)
        => new DatabricksBulkCopy().BulkCopy(ResolveBulkCopyType(options), table, options, source);

    /// <inheritdoc />
    public override Task<BulkCopyRowsCopied> BulkCopyAsync<T>(
        DataOptions options, ITable<T> table, IEnumerable<T> source, CancellationToken cancellationToken)
        => new DatabricksBulkCopy().BulkCopyAsync(ResolveBulkCopyType(options), table, options, source, cancellationToken);

    /// <inheritdoc />
    public override Task<BulkCopyRowsCopied> BulkCopyAsync<T>(
        DataOptions options, ITable<T> table, IAsyncEnumerable<T> source, CancellationToken cancellationToken)
        => new DatabricksBulkCopy().BulkCopyAsync(ResolveBulkCopyType(options), table, options, source, cancellationToken);

    private static BulkCopyType ResolveBulkCopyType(DataOptions options)
        => options.BulkCopyOptions.BulkCopyType == BulkCopyType.Default
            ? BulkCopyType.MultipleRows
            : options.BulkCopyOptions.BulkCopyType;
}
