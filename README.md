# GlutenFree ADO.NET Provider for Databricks

An open source, from-scratch **ADO.NET data provider for Databricks SQL warehouses**, plus a
**linq2db provider** built on top of it. The default transport is pure HTTP against
the Databricks [Statement Execution API](https://docs.databricks.com/api/workspace/statementexecution)
with Apache Arrow result decoding; an opt-in **Thrift (HiveServer2) transport add-on** brings real
server-side sessions. No ODBC Jank needed, pure .NET 8, async-first, injection-safe, and compatible with both Dapper as well as linq2db.

| Package | Description |
|---|---|
| `GlutenFree.Databricks.AdoNet` | Core ADO.NET provider (`DbConnection`/`DbCommand`/`DbDataReader`) |
| `GlutenFree.Databricks.AdoNet.Linq2Db` | [linq2db](https://github.com/linq2db/linq2db) data provider |
| `GlutenFree.Databricks.AdoNet.Linq2Db.Thrift` | linq2db provider flavor over the Thrift transport — enables interactive transactions |
| `GlutenFree.Databricks.AdoNet.Thrift` | Opt-in Thrift transport (real sessions), built on the [Apache Arrow ADBC Databricks driver](https://www.nuget.org/packages/Apache.Arrow.Adbc.Drivers.Databricks) |
| `GlutenFree.EntityFrameworkCore.Databricks` | Entity Framework Core 10 provider (preview — query pipeline; see [EF Core](#entity-framework-core-preview)) |

## Features

- **Pure .NET 8** — no native dependencies; talks straight to the REST Statement Execution API
- **Apache Arrow results** (`ARROW_STREAM` external links, LZ4-capable) with `JSON_ARRAY` fallback
- **Async-first**: `OpenAsync`, `ExecuteReaderAsync`, `ReadAsync` throughout; the sync API is
  a genuinely synchronous pipeline (`HttpClient.Send`), not sync-over-async — see
  [Async vs. sync](#async-vs-sync)
- **Server-side parameter binding** (`:name` markers) — inherently injection-safe, no client-side
  string substitution
- **PAT and OAuth M2M** (service principal) authentication, with cached, single-flighted token refresh
- **Faithful type mapping** including `DateOnly` for `DATE`, `SqlDecimal` for `DECIMAL` beyond
  .NET `decimal` precision, and complex types (`ARRAY`/`MAP`/`STRUCT`) as JSON strings
- **Retry with backoff** on 429/503 (honors `Retry-After`), statement cancel on timeout/cancellation
- **Works with Dapper** out of the box
- **linq2db provider**: LINQ queries, LATERAL joins, window functions, CTEs, MERGE upserts,
  batched bulk copy
- **Opt-in Thrift transport** (`GlutenFree.Databricks.AdoNet.Thrift`): real server-side
  sessions (`USE`/session state persists across commands) — see
  [Thrift transport](#thrift-transport-opt-in)
- **EF Core 10 provider** (preview): LINQ queries with Databricks-native SQL generation —
  see [Entity Framework Core](#entity-framework-core-preview)

## Async vs. sync

**Always prefer the async methods** (`OpenAsync`, `ExecuteReaderAsync`, `ExecuteNonQueryAsync`,
`ExecuteScalarAsync`, `ReadAsync`) — statement execution involves HTTP round-trips and
server-side polling, so async keeps threads free and scales far better under load.

That said, the synchronous API is a **first-class citizen**, not a `.Result` trap: sync calls
run a genuinely synchronous pipeline built on .NET's `HttpClient.Send`, with no sync-over-async
blocking anywhere. This means:

- No `SynchronizationContext` deadlocks (WinForms/WPF/legacy ASP.NET are safe)
- No thread-pool starvation from blocked async state machines
- Consumers that are inherently sync (Dapper's non-async API, `DataTable.Load`,
  linq2db's sync `ToList()`) work efficiently

Use sync when your caller is sync; use async everywhere else.

> **One documented exception:** when a transport hands us an Arrow stream that is not an
> `ArrowStreamReader` (possible with the opt-in [Thrift transport](#thrift-transport-opt-in)), the
> synchronous read path briefly blocks on an async read, because `Apache.Arrow`'s
> `IArrowArrayStream` declares only `ReadNextRecordBatchAsync` — there is no synchronous member to
> call. That block is centralised in a single helper that runs the work on the thread pool, so it
> still cannot deadlock against a UI or legacy-ASP.NET `SynchronizationContext`. The default REST
> transport is unaffected: it materialises bytes and uses `ArrowStreamReader`'s genuinely
> synchronous read.

## Quickstart (ADO.NET)

```csharp
using GlutenFree.Databricks.AdoNet;

await using var connection = new DatabricksConnection(
    "Host=https://adb-1234567890.11.azuredatabricks.net;" +
    "WarehouseId=abcdef1234567890;" +
    "Token=dapi...");
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT id, name FROM main.default.users WHERE created > :since";
command.Parameters.AddWithValue("since", new DateOnly(2026, 1, 1));

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt64(0)}: {reader.GetString(1)}");
}
```

### With Dapper

```csharp
using Dapper;

var users = await connection.QueryAsync<User>(
    "SELECT id, name FROM main.default.users WHERE name = :name",
    new { name = "alice" });
```

### With linq2db

```csharp
using GlutenFree.Databricks.AdoNet.Linq2Db;
using LinqToDB;

using var db = DatabricksTools.CreateDataConnection(connectionString);

var bigOrders = db.GetTable<Order>()
    .Where(o => o.Amount > 100m)
    .OrderByDescending(o => o.Amount)
    .Take(10)
    .ToList();

// MERGE upsert
db.GetTable<Order>()
    .Merge()
    .Using(newOrders)
    .OnTargetKey()
    .UpdateWhenMatched()
    .InsertWhenNotMatched()
    .Merge();
```

## Entity Framework Core (preview)

`GlutenFree.EntityFrameworkCore.Databricks` is an EF Core **10** provider (targets `net10.0`;
the package major tracks the EF Core major). It is a preview: the query pipeline works, and
`SaveChanges` and migrations are still being built out.

```csharp
using Microsoft.EntityFrameworkCore;

public class SalesContext(DbContextOptions<SalesContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

var options = new DbContextOptionsBuilder<SalesContext>()
    .UseDatabricks(connectionString, o => o.UseCatalog("main").UseSchema("sales"))
    .Options;

using var context = new SalesContext(options);

var bigOrders = await context.Orders
    .Where(o => o.Amount > 100m && o.Customer.StartsWith("acme"))
    .OrderByDescending(o => o.Amount)
    .Take(10)
    .ToListAsync();
```

`UseDatabricks` accepts a connection string or an existing `DatabricksConnection` (so the
Thrift transport can be opted into per context). `UseCatalog`/`UseSchema` override the
connection string's `Catalog`/`Schema` keywords.

What the provider generates is Databricks-native: backtick-quoted identifiers, `:name`
parameter markers, `LIMIT`/`OFFSET` paging (`LIMIT ALL` for an unbounded `Skip`), and
Databricks functions for the common `string`/date/`Math` translations
(`startswith`, `contains`, `length`, `year`, `upper`, …).

Preview caveats:

- **Keys must be client-generated.** Databricks cannot report store-generated values back to
  EF, so configure `ValueGeneratedNever()` (identity columns and concurrency tokens are not
  supported yet).
- **`SaveChanges` issues one statement per command.** With the REST transport it is therefore
  not atomic across entities; use the Thrift transport (real transactions) when you need
  atomicity. See [Current Limitations](#current-limitations).
- **Migrations are not implemented yet.** `EnsureCreated`/`EnsureDeleted` manage the Unity
  Catalog *schema*; full `Migrate()` support is planned.
- Constraints are informational in Delta, so EF's assumption that a primary key is unique is
  the application's responsibility.

## Thrift transport (opt-in)

The default REST transport is stateless: each statement is standalone. The
`GlutenFree.Databricks.AdoNet.Thrift` add-on package swaps in the Thrift (HiveServer2)
protocol — the same wire protocol the official JDBC/ODBC drivers use — via the
Databricks-maintained [Apache Arrow ADBC driver](https://www.nuget.org/packages/Apache.Arrow.Adbc.Drivers.Databricks).
The whole ADO.NET/Dapper/linq2db surface works unchanged; opt in per connection before opening:

```csharp
using GlutenFree.Databricks.AdoNet.Thrift;

await using var connection = new DatabricksConnection(connectionString)
    .UseThriftTransport();
await connection.OpenAsync();
```

What changes with Thrift:

- **Real server-side sessions** — one Thrift session per open connection. Catalog/schema
  context (`Catalog=`/`Schema=`, `ChangeDatabase`) is genuine session state, applied once
  via `USE` instead of replayed per statement.
- **All-purpose (interactive) clusters** — set `HttpPath` to the cluster endpoint
  (`/sql/protocolv1/o/<org-id>/<cluster-id>`); cluster endpoints only speak Thrift, so the
  default REST transport rejects them with guidance.

  > **⚠️ Cluster support is untested against a live cluster.** Our development and CI
  > environments run on Databricks Free Edition, which does not offer all-purpose
  > clusters, so we currently have no way to fully test this path end-to-end (warehouse
  > behavior *is* verified live; cluster tests are env-gated via
  > `DATABRICKS_CLUSTER_HTTP_PATH` and self-skip without one). If you hit issues testing
  > against a cluster, please [open an issue](../../issues): we're happy to prepare fix
  > branches and draft PRs for you to test against your cluster — we just can't finalize
  > a fix ourselves without someone verifying it live. Likewise, if you open a PR, we'll
  > gladly review it and run everything we *can* test (warehouse paths, CI) against your
  > branch. **Pull requests from users who can test against real clusters are very
  > welcome.**
- **Named parameters are emulated**: the ADBC driver exposes no native binding, so
  parameterized statements are wrapped in `EXECUTE IMMEDIATE '<sql>' USING CAST(...) AS name`.
  The server still resolves the `:name` markers (no client-side SQL rewriting of your
  statement); values are rendered only inside strictly escaped literals with validated
  names and type names.
- **Result streaming** (CloudFetch, LZ4) is handled inside the ADBC driver and surfaces
  through the same `DatabricksDataReader`.
- **Interactive transactions** — `BeginTransaction()` works (transactions are session
  state; see [Current Limitations](#current-limitations) for Databricks' requirements).
  For linq2db, use the `GlutenFree.Databricks.AdoNet.Linq2Db.Thrift` package, whose
  provider flavor declares transaction support and wires up the Thrift transport for you:

  ```csharp
  using GlutenFree.Databricks.AdoNet.Linq2Db.Thrift;

  using var db = DatabricksThriftTools.CreateDataConnection(connectionString);
  using var tx = db.BeginTransaction();
  db.Insert(new Order { /* ... */ });
  tx.Commit(); // or tx.Rollback(); disposing without committing rolls back
  ```
- The add-on carries heavier transitive dependencies (ApacheThrift and friends) — that's
  why it ships as a separate opt-in package rather than in the core provider.

Thrift integration coverage lives in its own project,
`GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests`, which re-runs the shared REST
integration suites **and the linq2db data-provider suites** over Thrift (via
subclassing — no duplicated test code) plus Thrift-only session-semantics tests. Run it
like any other test project with the usual `DATABRICKS_*` variables set; the base
integration projects stay REST-only and have no dependency on the Thrift add-on.

## Connection string reference

| Keyword | Default | Description |
|---|---|---|
| `Host` | *(required)* | Workspace URL, e.g. `https://adb-123.azuredatabricks.net` |
| `WarehouseId` or `HttpPath` | *(required)* | SQL warehouse id, or its HTTP path (`/sql/1.0/warehouses/<id>`); with the Thrift add-on, `HttpPath` may also be an all-purpose cluster endpoint (`/sql/protocolv1/o/<org-id>/<cluster-id>`) |
| `AuthType` | `Pat` | `Pat` or `OAuthM2M` |
| `Token` | — | Personal access token (when `AuthType=Pat`) |
| `ClientId` / `ClientSecret` | — | Service principal credentials (when `AuthType=OAuthM2M`) |
| `Catalog` / `Schema` | server default | Initial namespace for statements |
| `CommandTimeout` | `0` (server default) | Statement timeout, seconds |
| `ConnectTimeout` | `30` | Open/auth timeout, seconds |
| `ResultFormat` | `Arrow` | `Arrow` or `Json` |
| `Disposition` | `Auto` | `Auto`, `Inline` (JSON only), or `ExternalLinks` |
| `MaxRetries` | `4` | Retries for 429/503 responses |
| `RetryBaseDelay` | `500` | Base backoff delay, milliseconds |
| `Pooling` | `true` | Accepted for compatibility; the REST transport is stateless HTTP |

## Type mapping

| Databricks | .NET |
|---|---|
| `TINYINT` / `SMALLINT` / `INT` / `BIGINT` | `sbyte` / `short` / `int` / `long` |
| `FLOAT` / `DOUBLE` | `float` / `double` |
| `DECIMAL(p≤28,s)` | `decimal` |
| `DECIMAL(p>28,s)` | `System.Data.SqlTypes.SqlDecimal` (`GetSqlDecimal`, `GetString`), or the provider's arbitrary-precision `DatabricksDecimal` via `GetDatabricksDecimal` / `GetFieldValue<DatabricksDecimal>`; both types also bind as parameters |
| `STRING` / `CHAR` | `string` |
| `BOOLEAN` | `bool` |
| `BINARY` | `byte[]` |
| `DATE` | `DateOnly` (`GetDateOnly`; `GetDateTime` also works) |
| `TIMESTAMP` | `DateTime` (`Kind=Utc`) |
| `TIMESTAMP_NTZ` | `DateTime` (`Kind=Unspecified` — wall-clock value with no time zone; do not treat as UTC) |
| `ARRAY` / `MAP` / `STRUCT` / `VARIANT` / `INTERVAL` | `string` (JSON representation) |

## Current Limitations

- **The default REST transport is SQL-warehouse-only** — all-purpose (interactive)
  clusters only speak Thrift; use the `GlutenFree.Databricks.AdoNet.Thrift` add-on with a
  cluster `HttpPath` (see [Thrift transport](#thrift-transport-opt-in)). Note that cluster
  support is currently untested against a live cluster (not available on Free Edition) —
  report issues and we'll prepare test branches/draft PRs for you to verify against your
  cluster; PRs from users with cluster access are welcome, and we'll test what we can
  (warehouse paths, CI) on your branch.
- **Transactions require the Thrift transport** — Databricks supports interactive
  transactions (`BEGIN TRANSACTION` … `COMMIT`/`ROLLBACK`) as *session* state, so
  `BeginTransaction()` works only on the session-based
  [Thrift transport](#thrift-transport-opt-in); on the stateless REST transport it throws
  `NotSupportedException`. On either transport you can submit a self-contained
  `BEGIN ATOMIC … END;` block as a single statement for an atomic multi-statement unit
  of work. Databricks additionally requires every table written to in a transaction to be
  a Unity Catalog managed Delta/Iceberg table with catalog commits enabled, forbids
  DDL/metadata operations inside an interactive transaction, allows one transaction at a
  time per connection, and has no savepoints. Conflicts are detected optimistically at
  commit, so build retry logic. See the
  [Databricks transactions docs](https://docs.databricks.com/aws/en/transactions/).
  (The linq2db provider in `GlutenFree.Databricks.AdoNet.Linq2Db` declares
  `TransactionsSupported=false`, since its data provider is a singleton shared by both
  transports; for linq2db transactions use the `GlutenFree.Databricks.AdoNet.Linq2Db.Thrift`
  package's `DatabricksThriftTools`, whose provider flavor runs over the Thrift transport
  and declares `TransactionsSupported=true`.)
- **Input parameters only** — no output/return parameters, no stored procedures.
- **`BINARY` parameters unsupported** by the Statement Execution API — pass hex/base64
  strings and decode in SQL.
- Connection pooling is a no-op: the REST transport is stateless HTTP (HTTP handlers and
  OAuth tokens are shared internally where safe). The Thrift transport holds one session
  per open connection instead.

## Testing

```powershell
dotnet test   # integration tests skip unless DATABRICKS_* env vars are set
```

> **SDK prerequisite:** the repo uses the XML solution format (`.slnx`), which requires
> .NET SDK **9.0.200 or newer** to load (projects still target `net8.0`).

Live integration tests run against a real warehouse (Databricks Free Edition works) —
see [planning/integration-test-setup.md](planning/integration-test-setup.md).

## CI & releases

- **CI** (`.github/workflows/ci.yml`): every push/PR builds, runs tests (integration tests
  self-skip without credentials), and uploads pack artifacts.
- **Integration** (`.github/workflows/integration.yml`): manual dispatch; requires
  `DATABRICKS_HOST` / `DATABRICKS_TOKEN` / `DATABRICKS_WAREHOUSE_ID` repository secrets.
- **Release** (`.github/workflows/release.yml`): push a `v*` tag (e.g. `v0.1.0`) to build,
  test, pack with that version, push both packages to NuGet (requires the `NUGET_API_KEY`
  secret), and create a GitHub release. Packages include SourceLink, symbol packages
  (`.snupkg`), XML docs, and this README; the repository URL is inferred from the git
  remote at pack time, so it stays correct after the repo moves to its organization.

## License

[Apache License 2.0](LICENSE)
