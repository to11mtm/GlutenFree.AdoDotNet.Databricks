# Plan: linq2db transaction support over the Thrift transport

## 1. Background

The ADO.NET layer already supports interactive transactions (`BEGIN TRANSACTION` …
`COMMIT`/`ROLLBACK`) when the transport maintains a server-side session:
`DatabricksConnection.BeginDbTransaction` gates on
`IDatabricksTransport.SupportsTransactions` (`true` for `ThriftStatementTransport`,
`false` for the stateless REST transport) and returns a `DatabricksTransaction`.

The linq2db provider, however, hard-codes
`DatabricksDataProvider.TransactionsSupported => false`
(src/GlutenFree.Databricks.AdoNet.Linq2Db/Internal/DatabricksDataProvider.cs:56).
When that flag is `false`, linq2db's `DataConnection.BeginTransaction` returns a
no-op wrapper and never calls the underlying `DbConnection.BeginTransaction`
(verified by `Linq2DbProviderTests.BeginTransaction_is_noop_guarded_by_provider`).
So even a Thrift-backed connection silently gets *no* transaction through linq2db
today — arguably worse than throwing, since users may believe they are protected.

### Why the current singleton can't just flip the flag

`TransactionsSupported` is a **provider-level** property, but transport choice is a
**connection-level** decision made by `UseThriftTransport()` (an extension in the
separate `GlutenFree.Databricks.AdoNet.Thrift` add-on package that sets
`connection.TransportFactory`). The single `DatabricksTools.s_provider` instance is
shared by every connection regardless of transport, so declaring support globally
would make linq2db issue `BeginTransaction` on REST connections, which throws
`NotSupportedException`.

## 2. Design: two provider flavors

Introduce a second provider instance whose `TransactionsSupported` is `true`,
selected explicitly by users who know they are on the Thrift transport. Both
flavors remain process-wide singletons — we just stop pretending one instance fits
all transports.

### 2.1 Provider shape — constructor flag, not a subclass (recommended)

`DatabricksDataProvider` is `sealed`. Rather than unsealing it and adding a
`DatabricksThriftDataProvider` subclass, keep it sealed and add an internal
constructor parameter:

```csharp
public sealed class DatabricksDataProvider : DataProviderBase
{
    internal DatabricksDataProvider(string name, bool transactionsSupported)
        : base(name, DatabricksMappingSchema.Instance) { ... }

    public override bool TransactionsSupported => _transactionsSupported;
}
```

Rationale:
- Everything else about the provider (SQL builder, optimizer, mapping schema,
  bulk copy, schema provider) is transport-agnostic; a subclass would exist only
  to override one bool.
- Keeping the class sealed preserves the current public surface; the existing
  public parameterless ctor stays (delegating to the REST-flavored defaults) for
  back-compat, or is kept as the only public ctor.
- A subclass remains a fallback if we later need Thrift-specific SQL behavior,
  but nothing today requires it.

### 2.2 Provider naming

linq2db keys configurations, options caching, and provider registration by name,
so the two instances need distinct names:

```csharp
public static class DatabricksProviderName
{
    public const string Databricks = "Databricks";            // REST (default)
    public const string DatabricksThrift = "Databricks.Thrift"; // session-based
}
```

### 2.3 Where each flavor lives

The existing `GlutenFree.Databricks.AdoNet.Linq2Db` package keeps the REST-flavored
singleton and its `DatabricksTools` surface unchanged. The Thrift-flavored
singleton and its entry points live in the **new dedicated package** (§2.4):

```csharp
// GlutenFree.Databricks.AdoNet.Linq2Db (unchanged public surface):
private static readonly DatabricksDataProvider s_provider =
    new(DatabricksProviderName.Databricks, transactionsSupported: false);

// GlutenFree.Databricks.AdoNet.Linq2Db.Thrift — new DatabricksThriftTools:
private static readonly DatabricksDataProvider s_thriftProvider =
    new(DatabricksProviderName.DatabricksThrift, transactionsSupported: true,
        configureConnection: c => c.UseThriftTransport());

public static IDataProvider GetDataProvider();

// Turnkey connection-string flow — the provider configures the Thrift transport itself:
public static DataConnection CreateDataConnection(string connectionString);
public static DataOptions UseDatabricksThrift(this DataOptions options, string connectionString);

// Existing-connection flow (the connection must already have UseThriftTransport applied):
public static DataConnection CreateDataConnection(DatabricksConnection connection);
public static DataOptions UseDatabricksThrift(this DataOptions options, DatabricksConnection connection);
```

The internal `DatabricksDataProvider` ctor is exposed to the new assembly via
`InternalsVisibleTo` (or an `internal`-shared factory), keeping the ctor out of
the public API.

### 2.4 Decision: dedicated `GlutenFree.Databricks.AdoNet.Linq2Db.Thrift` package

`GlutenFree.Databricks.AdoNet.Linq2Db` references only the core ADO.NET package;
`UseThriftTransport()` lives in the Thrift add-on. The linq2db package therefore
cannot itself build a Thrift-configured connection from a connection string.

**Decision: ship a dedicated package** `GlutenFree.Databricks.AdoNet.Linq2Db.Thrift`
that references both `GlutenFree.Databricks.AdoNet.Linq2Db` and
`GlutenFree.Databricks.AdoNet.Thrift`, exposing the turnkey surface in §2.3.
(A configurator-delegate overload in the core linq2db package was considered and
rejected: it is easy to misconfigure, is not validated at setup time, and pushes
transport wiring onto every caller.)

Benefits:
- `UseDatabricksThrift(connectionString)` "just works": the provider applies
  `UseThriftTransport()` in its connection factory, so the flavor and transport
  can never disagree on the connection-string path.
- Both existing packages keep their dependency graphs unchanged; users who don't
  need Thrift transactions pull nothing new.
- The package is the natural home for Thrift-specific linq2db behavior if any
  ever appears (mirrors how the ADO.NET Thrift add-on is structured).

Cost: one more package to version/release — mitigated by the repo's existing
multi-package release workflow (release.yml packs all `src/` projects).

### 2.5 Failure mode when the flavor and transport disagree

The turnkey connection-string path cannot disagree (the provider configures the
transport itself). Remaining edge cases:

- Thrift-flavor provider + user-supplied connection *without*
  `UseThriftTransport()`: linq2db calls `DatabricksConnection.BeginTransaction`,
  which already throws a descriptive `NotSupportedException` pointing at
  `UseThriftTransport()`. Acceptable; optionally the `UseDatabricksThrift(connection)`
  overload can apply `UseThriftTransport()` defensively when the connection is
  unopened and has no transport factory set.
- REST-flavor provider + Thrift connection: today's silent no-op transaction.
  Unchanged, but the new package gives users the correct path. Doc callout only.

## 3. Behavior to verify / research items

1. **linq2db `BeginTransaction` plumbing** (verify against linq2db 6.4 sources):
   with `TransactionsSupported == true`, `DataConnection.BeginTransaction()` and
   `BeginTransaction(IsolationLevel)` call the ADO connection, store the
   `DbTransaction`, and attach it to every subsequent `DbCommand.Transaction`.
   Confirm the async paths (`BeginTransactionAsync`, `CommitTransactionAsync`,
   `RollbackTransactionAsync`) flow through `DatabricksTransaction`'s async members.
2. **Isolation level**: linq2db's parameterless `BeginTransaction()` and the
   `IsolationLevel` overload both work — `DatabricksTransaction` accepts any
   requested level and reports `Snapshot`. No mapping needed.
3. **Single transaction per session**: Databricks allows one active transaction
   per connection; linq2db also tracks a single current transaction per
   `DataConnection` — aligned. Nested `BeginTransaction` should surface the
   existing `InvalidOperationException`.
4. **DDL inside a transaction**: `CreateTable`/`DropTable`/temp-table APIs and
   `ChangeDatabase`/`ChangeCatalog` fail inside an interactive transaction
   (server-side or via the connection's `EnsureNoActiveTransaction` guard).
   Document; do not try to block in the provider.
5. **BulkCopy (MultipleRows)**: emits plain `INSERT`s through the same
   `DataConnection`, so it should enlist in the ambient transaction. Verify the
   command actually carries `Transaction` (linq2db sets it) and Thrift transport
   executes within the session.
6. **Retry policies**: if a linq2db retry policy is configured, `BeginTransaction`
   retries could leave a dangling server transaction. Note as a documented
   limitation (same caveat as any session-based provider).
7. **UC requirements**: transactions require Unity Catalog managed Delta/Iceberg
   tables with catalog commits — integration tests must create such tables (reuse
   whatever the ADO.NET Thrift transaction integration tests already do).

## 4. Implementation steps

1. `DatabricksProviderName`: add `DatabricksThrift` constant.
2. `DatabricksDataProvider`: add internal
   `(string name, bool transactionsSupported, Action<DatabricksConnection>? configureConnection)`
   ctor; `TransactionsSupported` returns the flag; `CreateConnectionInternal`
   applies the configurator; update the XML-doc remarks that currently explain
   why it is always `false`. Add `InternalsVisibleTo` for the new assembly.
3. New project `src/GlutenFree.Databricks.AdoNet.Linq2Db.Thrift`:
   - References `GlutenFree.Databricks.AdoNet.Linq2Db` +
     `GlutenFree.Databricks.AdoNet.Thrift` project references; same packaging
     metadata pattern as the other packable projects.
   - `DatabricksThriftTools` with the singleton + entry points from §2.3.
   - Add to the solution and confirm release.yml picks it up for pack/publish.
4. Docs:
   - README.md (§transactions, line ~225 currently states the provider always
     declares `TransactionsSupported=false`) and README.kawaii.md (line ~221).
   - planning/linq2db-dataprovider.md and planning/post-v0.1-backlog.md updates
     if they track this gap.
   - XML docs on all new members, including the UC-managed-table requirements
     (mirror `DatabricksTransaction`'s remarks).

## 5. Testing

Unit — new project `tests/GlutenFree.Databricks.AdoNet.Linq2Db.Thrift.Tests`
(FakeTransport with `SupportsTransactions = true`, injected via a pre-built
connection so no real Thrift session is needed):
- `BeginTransaction` executes `BEGIN TRANSACTION`; `CommitTransaction` → `COMMIT`;
  `RollbackTransaction` → `ROLLBACK`; dispose without commit → `ROLLBACK`.
- Statements inside the transaction carry the transaction (no extra control
  statements, correct ordering in `transport.ExecutedRequests`).
- Async variants (`BeginTransactionAsync` etc.).
- Thrift-flavor + non-session transport: `NotSupportedException` from the
  connection propagates.
- Both providers coexist: distinct names, queries on each generate identical SQL.
- `UseDatabricksThrift(connectionString)` produces a connection whose
  `TransportFactory` is the Thrift one (verifiable without opening).

Unit — existing tests/GlutenFree.Databricks.AdoNet.Linq2Db.Tests:
- `BeginTransaction_is_noop_guarded_by_provider` still passes for the REST
  flavor (rename to make the flavor explicit).

Integration — new project
`tests/GlutenFree.Databricks.AdoNet.Linq2Db.Thrift.IntegrationTests`, dedicated
to the new package (same env-var gating/config pattern as the other integration
projects, wired into integration.yml):
- linq2db insert + commit visible afterward; insert + rollback not visible;
  dispose-without-commit rolls back. Requires a UC managed Delta table (reuse
  the setup from the ADO.NET Thrift transaction integration tests).
- BulkCopy (MultipleRows) inside a transaction commits/rolls back atomically.
- Nested `BeginTransaction` throws; transaction after commit works (sequential
  transactions on one `DataConnection`).
- Optionally re-run the shared linq2db suites over the Thrift provider flavor
  via the unsealed-base-class rerun pattern used by
  GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests.

The existing GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests project stays
focused on the ADO.NET-level Thrift transport; no linq2db tests are added there.

## 6. Out of scope

- Savepoints (`DatabricksTransaction.SupportsSavepoints == false`).
- Distributed/ambient (`TransactionScope`) enlistment.
- Auto-detecting the transport to pick the provider flavor at runtime (transport
  is resolved at `Open()`, after linq2db has committed to a provider).
- Configurator-delegate overloads in the core linq2db package (rejected in §2.4
  in favor of the dedicated package).
