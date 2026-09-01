# Sync-over-Async Audit — `.GetAwaiter().GetResult()` (｡•̀ᴗ-)✧

> Status: **implemented (2026-09-01)**. All five steps below are done; see
> [Outcome](#outcome-as-implemented).
> Date: 2026-08-31 · Revised: 2026-09-01 (open questions answered, plan executed)
> Target framework: `net8.0` (single TFM today; `netstandard2.0` is *deferred, not ruled out* — see D2)

---

## TL;DR

There are **9** live `.GetAwaiter().GetResult()` call sites, all in `src/` (zero in tests). They fall into
**four risk buckets**:

| Bucket | Sites | Real deadlock risk? | Notes |
| --- | --- | --- | --- |
| 🟢 **A. Fake-async** (awaited work always completes synchronously) | 3 | **No** | Blocks on an already-completed task. Cheap to remove properly. |
| 🟡 **B. Default interface members** (opt-in escape hatch) | 4 | Only if an implementer doesn't override | REST + Thrift both override the hot one; the rest are unimplemented/never-called. |
| 🟠 **C. Genuinely async work blocked on** | 1 | **Yes** | `ThriftStatementTransport.ExecuteStatement` → `BuildResponseAsync`. |
| 🔴 **D. Third-party async-only API** | 1 | **Yes** (bounded) | `IArrowArrayStream.ReadNextRecordBatchAsync` — no sync API exists upstream. |

The deadlock story is much better than the raw count suggests: `ConfigureAwait(false)` is used **consistently**
(20+ occurrences) on every internal await, so the classic ASP.NET-classic / WinForms `SynchronizationContext`
deadlock is largely defanged. The residual risks are (1) **thread-pool starvation** under load, and
(2) library code we `await` into that we don't control (`Apache.Arrow`, ADBC driver) potentially not using
`ConfigureAwait(false)` internally.

---

## Call-site inventory

### 🟢 Bucket A — Awaited work is already synchronous

These block on a `ValueTask`/`Task` that is **guaranteed complete** by the time we block. They're pure noise
and can be removed with near-zero behavioural risk.

#### A1. `DatabricksConnection.cs:131` — `Open()` failure cleanup

```csharp
catch
{
    DisposeTransportAsync().AsTask().GetAwaiter().GetResult();  // ← here
    _state = ConnectionState.Closed;
    throw;
}
```

`DisposeTransportAsync()` → `_transport.DisposeAsync()`. Both implementations are synchronous today:

- `RestStatementTransport.DisposeAsync()` → `Dispose(); return ValueTask.CompletedTask;` (and `Dispose()` is a **no-op**)
- `ThriftStatementTransport.DisposeAsync()` → returns `ValueTask.CompletedTask`

Extra cost: `.AsTask()` allocates a `Task` just to throw it away. 🗑️

#### A2. `DatabricksConnection.cs:182` — `Close()`

```csharp
public override void Close() => CloseAsync().GetAwaiter().GetResult();
```

`CloseAsync()` = `DisposeTransportAsync()` + set state. Same story as A1 — **completes synchronously**.
Also reached from `Dispose(bool disposing)` (line 251), so **every `using (var conn = ...)` block hits this**.
That makes it the single most-executed sync-over-async site in the library.

#### A3. `ThriftStatementTransport.cs:150` — session-context replay

```csharp
ApplySessionContextAsync(request, commandTimeout, cancellationToken, sync: true)
    .GetAwaiter().GetResult();
```

Note the `sync: true` flag: it already threads down to `ExecuteUseAsync(..., sync)` which calls
`statement.ExecuteUpdate()` (**genuinely sync**) instead of `ExecuteUpdateAsync()`. So the returned `Task`
is *always* already completed — this is an `async` method that never actually yields on the sync path.
It's Bucket A **in practice**, but the compiler can't prove it and a future edit could silently break that.

---

### 🟡 Bucket B — Default interface members (escape hatches — **being deleted**, see D1 / Step 2)

#### B1. `Transport/IDatabricksTransport.cs:39` — `ExecuteStatement`

```csharp
StatementResponse ExecuteStatement(
    StatementRequest request, TimeSpan commandTimeout, CancellationToken cancellationToken)
    => ExecuteStatementAsync(request, commandTimeout, cancellationToken).GetAwaiter().GetResult();
```

**Both in-box transports override this** (`RestStatementTransport` with genuinely sync HTTP,
`ThriftStatementTransport` with sync ADBC). The DIM only fires for third-party/test transports.

#### B2/B3. `IDatabricksTransport.cs:43, 47` — `GetResultChunk`, `DownloadExternalLink`

Same shape. Note that on the Thrift transport the *async* versions both `throw new NotSupportedException(...)`,
so blocking on them is moot there.

#### B4. `Auth/IDatabricksAuthenticator.cs:19` — `GetToken`

```csharp
string GetToken(CancellationToken cancellationToken = default)
    => GetTokenAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
```

`DatabricksConnection.Open()` (line 124) calls `GetToken(...)` to eagerly acquire credentials, so this sits on
the synchronous connection-open path. **Audited ✅ — both in-box authenticators override it:**

- `PatAuthenticator.cs:22` → `public string GetToken(...) => _token;` (trivially sync)
- `OAuthM2MAuthenticator.cs:91` → overrides with its own implementation (verify it uses sync HTTP, not a nested block)

⚠️ Residual risk is only for **custom** `IDatabricksAuthenticator` implementations that skip the override —
those would block the entire sync `Open()` on a real OAuth token-endpoint round-trip.
**Resolved by D1:** custom implementations are not a supported scenario, so the DIM becomes abstract (Step 2).

---

### 🟠 Bucket C — Blocking on genuinely async work

#### C1. `ThriftStatementTransport.cs:167` — `BuildResponseAsync`

```csharp
var result = statement.ExecuteQuery();
cancellationToken.ThrowIfCancellationRequested();
// Blocking on the async path here is acceptable: the initial batch peek is a
// short read, and passing the real token keeps sync cancellation behavior
// consistent with ExecuteStatementAsync.
return BuildResponseAsync(statementId, statement, result, cancellationToken)
    .GetAwaiter().GetResult();
```

This is the **real one**. `BuildResponseAsync` peeks the first Arrow record batch, which is a genuine network
read against the ADBC/Thrift stream. The existing comment argues the read is short — true, but "short" is not
"non-blocking", and the awaited code path descends into `Apache.Arrow` / ADBC where we can't guarantee
`ConfigureAwait(false)`.

---

### 🔴 Bucket D — Third-party API has no sync counterpart

#### D1. `DatabricksDataReader.cs:755` — `ReadNextBatchSync`

```csharp
private static RecordBatch? ReadNextBatchSync(IArrowArrayStream stream)
    => stream is ArrowStreamReader reader
        ? reader.ReadNextRecordBatch()              // genuinely sync fast path ✅
        : stream.ReadNextRecordBatchAsync().GetAwaiter().GetResult();  // ← here
```

Already well mitigated: the common case (`ArrowStreamReader`, i.e. REST transport materialised bytes) takes
the genuinely synchronous branch. Only *streaming* `IArrowArrayStream` implementations (Thrift) fall through.
`Apache.Arrow`'s `IArrowArrayStream` interface **only** exposes `ReadNextRecordBatchAsync` — there is no sync
member to call, so this cannot be fully removed without upstream changes.

---

### ℹ️ Non-issues (matched the grep, not actually sync-over-async)

- `DatabricksDataReader.cs:54, 56` — `response.Result` is a **`ResultData` property** on `StatementResponse`, not `Task.Result`. False positive. 😌

---

## Decisions (answered 2026-09-01)

| # | Question | Answer | Consequence |
| --- | --- | --- | --- |
| **D1** | Are third-party `IDatabricksTransport` / `IDatabricksAuthenticator` implementations supported? | **No** — we are still early-adopter phase. | The DIM escape hatches protect nobody. **Option 2 is free**, and we take its *strong* form: delete the defaults, make the sync members abstract. |
| **D2** | Is `net8.0`-only permanent? | **No** — `netstandard2.0` stays on the table; we ship `net8.0`-only for now and add it later if needed. | Prefer designs that *do not depend on DIMs*, since `netstandard2.0` has none. Deleting the DIMs (D1) is therefore doubly good: it removes the single largest `netstandard2.0` blocker in `3c` of `planning/netstandard20-support-plan.md`. Keep `ValueTask`/`IAsyncDisposable` (both polyfillable via `System.Threading.Tasks.Extensions` / `Microsoft.Bcl.AsyncInterfaces`). |
| **D3** | Open an upstream `Apache.Arrow` issue for a sync `IArrowArrayStream.ReadNextRecordBatch`? | **Not at this time.** | D1 (the Arrow read) is accepted as permanent. It gets containment, not a fix. |
| **D4** | Do we care about `SynchronizationContext` deadlocks (WinForms/WPF/legacy ASP.NET callers)? | **Yes.** | **Option 4b** — the containment helper offloads to `TaskScheduler.Default` rather than blocking inline. |

Net effect: the "pick one" menu collapses to **do Options 1 + 2(strong) + 3 + 4b, skip Option 5 as a
standalone** (it survives only as one README paragraph in step 5 below).

---

## Work plan

### Step 1 — 🟢 Remove Bucket A (sync disposal + sync session context)

Eliminates A1, A2, A3. The awaited work is already synchronous, so this is mechanical.

- Add `IDisposable` to `IDatabricksTransport` (it is already `IAsyncDisposable`). No DIM — declare
  `void Dispose()` as a real interface member. `RestStatementTransport` already implements it;
  `ThriftStatementTransport` needs a one-line no-op-equivalent.
- Add a private `void DisposeTransport()` in `DatabricksConnection` mirroring `DisposeTransportAsync()`,
  and use it from the `Open()` catch block (A1).
- Rewrite `Close()` as a genuinely synchronous method (A2); `CloseAsync()` becomes
  `{ Close(); return Task.CompletedTask; }` since nothing async remains. `Dispose(bool)` routes to `Close()`.
- Split `ApplySessionContextAsync(..., sync: true)` into a real `ApplySessionContext()` sync helper
  (A3), removing the `sync` bool from that pair.

**Risk:** very low. **Bonus:** deletes a `Task` allocation from every `using (var conn = ...)` block.
**Note for D2:** `ValueTask.CompletedTask` needs a polyfill on `netstandard2.0`; a sync `Dispose()` path
means fewer places that need one.

### Step 2 — 🟡 Delete the DIM escape hatches (B1–B4)

Per **D1**, third-party implementers are not a supported scenario, so this is not a breaking change for
anyone we care about.

- `IDatabricksTransport`: make `ExecuteStatement`, `GetResultChunk`, `DownloadExternalLink` **abstract**
  (drop the `=> ...GetAwaiter().GetResult()` bodies).
- `IDatabricksAuthenticator`: make `GetToken` **abstract**.
- Fill in the now-required implementations:
  - `RestStatementTransport` — already overrides all three; just drop the `override`-by-DIM semantics.
  - `ThriftStatementTransport` — already overrides `ExecuteStatement`; `GetResultChunk` /
    `DownloadExternalLink` become explicit `throw new NotSupportedException(...)`, matching what their
    async counterparts already do. **Fails loud, not slow.**
  - `PatAuthenticator` / `OAuthM2MAuthenticator` — already implement `GetToken`. Confirm
    `OAuthM2MAuthenticator.GetToken` uses genuinely sync HTTP and is not a nested block.
- Add a unit test asserting no `IDatabricksTransport` / `IDatabricksAuthenticator` member is left
  unimplemented-by-design (or simply rely on the compiler — abstract members make this a build error, which
  is the whole point).

**Removes:** B1–B4 (4 of 9). **Risk:** low; compiler-enforced.

### Step 3 — 🟠 Sync-ify the Thrift response build (C1)

Give `BuildResponseAsync` a sync sibling, mirroring the `sync` flag convention already used elsewhere in
`ThriftStatementTransport`:

```csharp
private StatementResponse BuildResponse(string id, AdbcStatement s, QueryResult r, CancellationToken ct);
private Task<StatementResponse> BuildResponseAsync(...);
```

This does not *remove* blocking — the first-batch peek bottoms out in `IArrowArrayStream.ReadNextRecordBatchAsync`
(D1), which per **D3** will not gain a sync counterpart. It pushes the block down into the single
unavoidable, well-documented site.

**Removes:** C1 as a distinct site. **Risk:** medium — Thrift hot path, needs integration-test coverage
(`GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests`).

### Step 4 — 🔴 Contain the last one behind `SyncOverAsync` (4b, offload flavour)

Per **D4** we *do* care about `SynchronizationContext` deadlocks, so the helper offloads rather than
blocking inline on the caller's context:

```csharp
/// <summary>
/// Blocks on a genuinely-async operation. Used ONLY where no synchronous API exists upstream.
/// Runs the work on the thread pool (TaskScheduler.Default, DenyChildAttach) so that a UI or
/// legacy-ASP.NET SynchronizationContext on the calling thread cannot deadlock us.
/// CopilotNote: every use of this MUST cite the upstream API that lacks a sync overload.
/// </summary>
internal static class SyncOverAsync
{
    public static T Run<T>(Func<Task<T>> work);
    public static T Run<T>(Func<ValueTask<T>> work);
}
```

Implementation notes:

- Use `TaskFactory(CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskContinuationOptions.None, TaskScheduler.Default)`
  and `.Unwrap().GetAwaiter().GetResult()`.
- Keep `GetAwaiter().GetResult()` **inside** the helper — it unwraps `AggregateException` correctly;
  `.Result` / `.Wait()` do not.
- Cost: one extra thread-pool hop. Accepted — correctness under a captured `SynchronizationContext` wins,
  and this is now a cold, once-per-batch path.
- Sole caller: `DatabricksDataReader.ReadNextBatchSync` (D1), *after* its existing `ArrowStreamReader`
  fast path — the common REST case still never touches the helper.
- **D2 bonus:** one file to adjust if/when `netstandard2.0` lands.

### Step 5 — 🚫 Document the residual (the surviving sliver of Option 5)

One README/ADR paragraph: *"On the Thrift transport, synchronous ADO.NET members may block briefly on
async I/O because `Apache.Arrow`'s `IArrowArrayStream` is async-only. Prefer the `*Async` members."*
Plus the `// CopilotNote:` policy comment on `SyncOverAsync`.

---

## Outcome (as implemented)

| Bucket | Before | After |
| --- | --- | --- |
| 🟢 A — fake-async | 3 | **0** (real sync paths) |
| 🟡 B — DIM escape hatches | 4 | **0** (abstract; compiler-enforced) |
| 🟠 C — Thrift response build | 1 | **0** (folded into D) |
| 🔴 D — Arrow async-only | 1 | **0 raw sites**; 2 calls to `SyncOverAsync.Run`, offloaded per 4b |
| **Total raw `.GetAwaiter().GetResult()` in `src/`** | **9** | **2**, both inside `SyncOverAsync` itself |

`SyncOverAsync.Run` has exactly two callers, both blocked on the *same* upstream gap
(`IArrowArrayStream` has no synchronous `ReadNextRecordBatch`):

- `DatabricksDataReader.ReadNextBatchSync` — only for streaming (non-`ArrowStreamReader`) streams;
  the REST path still takes the genuinely synchronous branch.
- `ThriftStatementTransport.BuildResponse` — the first-batch peek.

Greppable, documented, deadlock-hardened, and squarely blamed on upstream. ✨

### What changed in code

| File | Change |
| --- | --- |
| `Transport/IDatabricksTransport.cs` | Now `IAsyncDisposable, IDisposable`; `ExecuteStatement` / `GetResultChunk` / `DownloadExternalLink` are abstract (no DIM bodies). |
| `Auth/IDatabricksAuthenticator.cs` | `GetToken` is abstract (no DIM body). |
| `DatabricksConnection.cs` | New private `DisposeTransport()`; `Close()` is genuinely sync; `Open()`'s catch uses the sync path. |
| `Internal/SyncOverAsync.cs` | **New.** `TaskFactory` on `TaskScheduler.Default` + `DenyChildAttach`, `Task<T>` and `ValueTask<T>` overloads. |
| `DatabricksDataReader.cs` | `ReadNextBatchSync` routes the streaming branch through `SyncOverAsync.Run`. |
| `Thrift/ThriftStatementTransport.cs` | Sync `Dispose()` (async delegates to it); sync `GetResultChunk`/`DownloadExternalLink` throwing `NotSupportedException`; `sync` bool flag replaced by real `ApplySessionContext`/`ExecuteUse` siblings; `BuildResponse` sync sibling sharing `BuildEmptyResponse`/`BuildStreamingResponse`. |
| `Transport/RestStatementTransport.cs` | Dropped the now-redundant explicit `IDisposable`. |
| `tests/.../FakeTransport.cs` | Implements the sync members + `Dispose()`; tracks `DisposedSynchronously`. |
| `tests/.../SyncPathTests.cs` | New tests: sync `Close`/`Dispose` use the sync transport path; `SyncOverAsync` survives a never-pumped `SynchronizationContext`, unwraps exceptions, and supports `ValueTask`. |

Verified: full solution builds warning-free; 298 → 303 unit tests, all passing. Integration tests
(warehouse-gated) were not run as part of this change.

## Deferred / not doing

- **Upstream `Apache.Arrow` issue** for a sync `ReadNextRecordBatch` (D3) — revisit if the Thrift
  transport's sync path shows up in profiling.
- **`netstandard2.0` multi-targeting** (D2) — tracked separately in
  `planning/netstandard20-support-plan.md`. Steps 1, 2 and 4 all move that plan forward for free.
- **Keeping DIMs with `[Obsolete]` doc warnings** — superseded by D1; abstract members are strictly better
  while there are no external implementers.
