namespace GlutenFree.Databricks.AdoNet.Internal;

/// <summary>
/// Blocks on a genuinely asynchronous operation. Used <b>only</b> where the upstream API exposes
/// no synchronous counterpart, so that every such block is greppable from one place.
/// </summary>
/// <remarks>
/// The work is started on the thread pool (<see cref="TaskScheduler.Default"/>, with
/// <see cref="TaskCreationOptions.DenyChildAttach"/>) rather than inline, so a UI or
/// legacy-ASP.NET <see cref="SynchronizationContext"/> captured on the calling thread cannot
/// deadlock the continuation against the thread we are blocking. The cost is one extra
/// thread-pool hop.
/// <para>
/// CopilotNote: every use of this helper MUST cite the upstream API that lacks a sync overload.
/// If a synchronous API exists, call it instead — do not use this helper for convenience.
/// </para>
/// </remarks>
internal static class SyncOverAsync
{
    private static readonly TaskFactory s_factory = new(
        CancellationToken.None,
        TaskCreationOptions.DenyChildAttach,
        TaskContinuationOptions.None,
        TaskScheduler.Default);

    /// <summary>Runs <paramref name="work"/> off the caller's context and blocks for its result.</summary>
    public static T Run<T>(Func<Task<T>> work)
        => s_factory.StartNew(work).Unwrap().GetAwaiter().GetResult();

    /// <summary>Runs <paramref name="work"/> off the caller's context and blocks for its result.</summary>
    public static T Run<T>(Func<ValueTask<T>> work)
        => s_factory.StartNew(() => work().AsTask()).Unwrap().GetAwaiter().GetResult();
}
