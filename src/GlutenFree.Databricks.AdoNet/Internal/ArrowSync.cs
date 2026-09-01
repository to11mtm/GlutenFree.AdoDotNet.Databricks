using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace GlutenFree.Databricks.AdoNet.Internal;

/// <summary>
/// Synchronous reads over Apache Arrow streams, used by the synchronous ADO.NET surface.
/// </summary>
internal static class ArrowSync
{
    /// <summary>
    /// Reads the next record batch synchronously. Streams that can serve a batch without async
    /// work (<see cref="ISyncArrowArrayStream"/> wrappers) and <see cref="ArrowStreamReader"/>,
    /// which exposes a genuinely synchronous read, are used directly; other
    /// <see cref="IArrowArrayStream"/> implementations (streaming transports) declare only
    /// <c>ReadNextRecordBatchAsync</c>, so those go through <see cref="SyncOverAsync"/>.
    /// </summary>
    /// <remarks>
    /// The token is honoured only on the async path: <see cref="ArrowStreamReader"/>'s
    /// synchronous read takes no cancellation token.
    /// </remarks>
    public static RecordBatch? ReadNextBatch(
        IArrowArrayStream stream, CancellationToken cancellationToken = default)
        => stream switch
        {
            ISyncArrowArrayStream sync => sync.ReadNextRecordBatch(cancellationToken),
            ArrowStreamReader reader => reader.ReadNextRecordBatch(),
            _ => SyncOverAsync.Run(() => stream.ReadNextRecordBatchAsync(cancellationToken)),
        };
}
