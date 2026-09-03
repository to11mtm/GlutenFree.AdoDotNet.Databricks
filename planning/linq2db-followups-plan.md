# Plan: linq2db follow-ups (optional items)

Optional work identified after shipping `GlutenFree.Databricks.AdoNet.Linq2Db.Thrift`
(see planning/linq2db-thrift-transactions-plan.md, implemented). None of these block
current functionality; they are captured here for future prioritization. No code has
been changed for any item yet.

## 1. DI-friendly registration (linq2db semantics)

### 1.1 Background: how linq2db 6.x DI works

- DI integration lives in the **`linq2db.Extensions`** NuGet package (v6 rename of
  `linq2db.AspNet`), namespace `LinqToDB.Extensions.DependencyInjection`, via
  `AddLinqToDBContext<TContext>(this IServiceCollection, Func<IServiceProvider, DataOptions, DataOptions> configure, ServiceLifetime lifetime = Scoped)`
  (plus `Func<DataOptions, DataOptions>`, `Func<DataOptions>`, factory-delegate, and
  `<TContext, TContextImplementation>` variants). Each call registers
  `DataOptions<TContext>`, the context itself, and an `IDataContextFactory<TContext>`.
- The recommended context shape is constructor injection of the typed options wrapper
  (`DataOptions<T>` ships in the core `linq2db` package, so no extra dependency for us):

  ```csharp
  public class AppDataConnection : DataConnection
  {
      public AppDataConnection(DataOptions<AppDataConnection> options)
          : base(options.Options) { }
  }
  ```
- **Provider instances need no global registration**: `DataOptions.UseConnectionString(IDataProvider, string)` /
  `UseConnection(IDataProvider, DbConnection)` assign the provider directly
  (`DataConnection.ConfigurationApplier.Apply` only hits the name registry for the
  `ProviderName`-string path). Our existing `UseDatabricks` / `UseDatabricksThrift`
  `DataOptions` extensions already follow the idiomatic `Use<Database>` shape, so
  **this works with DI today**:

  ```csharp
  services.AddLinqToDBContext<AppDataConnection>(
      (provider, options) => options.UseDatabricksThrift(connectionString));
  ```
- Name-based resolution (`DataOptions.UseProvider("Databricks.Thrift")`,
  `UseConnectionString("Databricks.Thrift", cs)`, `new DataConnection("ConfigName")`,
  appsettings-style `ILinqToDBSettings`, and
  `IDataContextFactory<TContext>.CreateDataContext(string? configuration)`) resolves
  via `DataConnection.GetDataProviderEx` — which consults the registry populated by
  `DataConnection.AddDataProvider(string, IDataProvider)` (a stable public API in
  `LinqToDB.Data`, not `Internal`) and registered provider detectors. Neither of our
  flavors is registered there today, so those paths throw
  `LinqToDBException("DataProvider 'Databricks…' not found.")`.
- linq2db has no keyed-service story; multi-database apps make one
  `AddLinqToDBContext<TContext>` call per flavor with distinct context types —
  `DataOptions<T>` exists precisely to disambiguate those registrations.

### 1.2 Proposed work

**A. Document the works-today DI pattern (no code).** README section showing
`AddLinqToDBContext` with `UseDatabricks`/`UseDatabricksThrift` and the
`DataOptions<TContext>` constructor pattern, including a two-flavor example:

```csharp
public sealed class ReportsDb(DataOptions<ReportsDb> options)   // REST, read paths
    : DataConnection(options.Options);

public sealed class IngestDb(DataOptions<IngestDb> options)     // Thrift, transactional writes
    : DataConnection(options.Options);

services.AddLinqToDBContext<ReportsDb>((sp, o) => o.UseDatabricks(restConnectionString));
services.AddLinqToDBContext<IngestDb>((sp, o) => o.UseDatabricksThrift(thriftConnectionString));
```

**B. Explicit name registration for string/config-based resolution.** Add idempotent
registration entry points (explicit call, not module initializer — provider
registration is process-global mutable state, and linq2db's own providers are opt-in
via `Use*`/detectors too):

```csharp
// GlutenFree.Databricks.AdoNet.Linq2Db
DatabricksTools.RegisterDataProvider();       // AddDataProvider("Databricks", s_provider)

// GlutenFree.Databricks.AdoNet.Linq2Db.Thrift
DatabricksThriftTools.RegisterDataProvider(); // AddDataProvider("Databricks.Thrift", s_thriftProvider)
```

Notes:
- `DataConnection.AddDataProvider` overwrites into a `ConcurrentDictionary`, so
  repeated calls are naturally idempotent.
- Optionally also `DataConnection.AddConfiguration(...)`-based examples for
  `new DataConnection("ConfigName")` / appsettings scenarios in docs.
- Consider registering a provider detector (`DataConnection.AddProviderDetector`)
  matching `ConnectionOptions.ProviderName == "Databricks"` / `"Databricks.Thrift"`
  as an alternative — but the explicit `AddDataProvider` registry is simpler and
  sufficient; detectors buy nothing extra here since our connection strings carry no
  transport marker to sniff.
- Unit tests: after registration, `new DataOptions().UseConnectionString("Databricks.Thrift", cs)`
  resolves the Thrift flavor and produces a Thrift-configured connection;
  double registration is harmless; the two names resolve to distinct instances.

**C. No new package dependency.** Do *not* reference `linq2db.Extensions` (or
Microsoft.Extensions.DependencyInjection) from the provider packages — `AddLinqToDBContext`
already accepts our `DataOptions` extensions, and `DataOptions<T>` lives in core
linq2db. A `GlutenFree…Linq2Db.Extensions` convenience package (e.g. an
`AddDatabricksLinqToDB<TContext>()` wrapper) is rejected for now: it would only save
one lambda and adds a package to version, while pinning us to `linq2db.Extensions`'
release cadence.

**D. `IDataContextFactory<TContext>` caveat (docs only).** Its
`CreateDataContext(string? configuration)` overload flows through configuration-name
resolution, so it needs item B (plus `AddConfiguration`) to work with a non-null
configuration string; with `null` it uses the registered options and works today.

## 2. Shared linq2db suite rerun under the Thrift provider flavor

**Current coverage.** `GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests` already
re-runs the linq2db integration suites over the Thrift *transport* — but with the
REST-flavor provider instance (`DatabricksTools` + `IntegrationConfig.ConnectionCustomizer`).
The `Databricks.Thrift`-named flavor is exercised by its own dedicated suite
(`…Linq2Db.Thrift.IntegrationTests`) but not by the full shared suites.

**Assessment: low value.** Both flavors are the same sealed class sharing one SQL
builder/optimizer/mapping pipeline; only `TransactionsSupported`, the provider name,
and connection configuration differ, and those are covered by the dedicated suite.

**If pursued:** add rerun subclasses (unsealed-base-class pattern) in
`…Linq2Db.Thrift.IntegrationTests` with a hook in the linq2db integration base classes
to substitute the provider (they currently call `DatabricksTools.CreateDataConnection`
directly — a `Func<DatabricksConnection, DataConnection>` customizer on the base
class, mirroring `IntegrationConfig.ConnectionCustomizer`, would be needed). Weigh
against doubled live-test runtime (~4 minutes today).

## 3. Retry policies vs. `BeginTransaction`

**Caveat (documented, not enforced).** If a linq2db `IRetryPolicy` retries a failed
`BeginTransaction`, a first attempt that succeeded server-side but failed to respond
could leave the session with an open transaction, making the retry fail with
"transaction already active" semantics. This is inherent to session-based
transactions; the ADO.NET layer already restricts to one active transaction per
connection.

**Possible hardening (only if users report it):**
- Issue a best-effort `ROLLBACK` before retrying `BEGIN TRANSACTION`, or
- Detect the server's "transaction already active" error in `BeginDbTransaction` and
  surface a clearer message suggesting a rollback/reconnect.
- Add a README/XML-doc note advising against wrapping `BeginTransaction` in generic
  retry policies.

## 4. Explicitly out of scope (re-affirmed)

- **Savepoints** — Databricks has none (`DatabricksTransaction.SupportsSavepoints == false`).
- **`TransactionScope` / ambient enlistment** — no distributed transaction support.
- **Auto-detecting transport to pick the provider flavor** — transport is resolved at
  `Open()`, after linq2db has committed to a provider instance.
