using Apache.Arrow;

namespace GlutenFree.Databricks.AdoNet.Internal;

/// <summary>
/// Implemented by <see cref="Apache.Arrow.Ipc.IArrowArrayStream"/> wrappers that can serve a batch
/// without going through their asynchronous read, so <see cref="ArrowSync.ReadNextBatch"/> can
/// avoid blocking. Wrappers should delegate to <see cref="ArrowSync.ReadNextBatch"/> for the
/// stream they wrap, so the decision is made against the real underlying stream.
/// </summary>
internal interface ISyncArrowArrayStream
{
    /// <summary>Reads the next batch synchronously, or <c>null</c> when the stream is drained.</summary>
    RecordBatch? ReadNextRecordBatch(CancellationToken cancellationToken = default);
}
