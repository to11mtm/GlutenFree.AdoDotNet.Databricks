using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Internal.DataProvider;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// Bulk copy for Databricks: there is no native driver bulk-copy API, so both
/// <see cref="BulkCopyType.ProviderSpecific"/> and <see cref="BulkCopyType.MultipleRows"/>
/// produce a batched multi-row <c>INSERT INTO ... VALUES (...), (...)</c> statement.
/// </summary>
public class DatabricksBulkCopy : BasicBulkCopy
{
    /// <inheritdoc />
    protected override BulkCopyRowsCopied MultipleRowsCopy<T>(
        ITable<T> table, DataOptions options, IEnumerable<T> source)
        => MultipleRowsCopy1(table, options, source);

    /// <inheritdoc />
    protected override Task<BulkCopyRowsCopied> MultipleRowsCopyAsync<T>(
        ITable<T> table, DataOptions options, IEnumerable<T> source, CancellationToken cancellationToken)
        => MultipleRowsCopy1Async(table, options, source, cancellationToken);

    /// <inheritdoc />
    protected override Task<BulkCopyRowsCopied> MultipleRowsCopyAsync<T>(
        ITable<T> table, DataOptions options, IAsyncEnumerable<T> source, CancellationToken cancellationToken)
        => MultipleRowsCopy1Async(table, options, source, cancellationToken);

    /// <inheritdoc />
    protected override BulkCopyRowsCopied ProviderSpecificCopy<T>(
        ITable<T> table, DataOptions options, IEnumerable<T> source)
        => MultipleRowsCopy1(table, options, source);

    /// <inheritdoc />
    protected override Task<BulkCopyRowsCopied> ProviderSpecificCopyAsync<T>(
        ITable<T> table, DataOptions options, IEnumerable<T> source, CancellationToken cancellationToken)
        => MultipleRowsCopy1Async(table, options, source, cancellationToken);

    /// <inheritdoc />
    protected override Task<BulkCopyRowsCopied> ProviderSpecificCopyAsync<T>(
        ITable<T> table, DataOptions options, IAsyncEnumerable<T> source, CancellationToken cancellationToken)
        => MultipleRowsCopy1Async(table, options, source, cancellationToken);
}
