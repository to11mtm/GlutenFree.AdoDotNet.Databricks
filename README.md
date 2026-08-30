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
| `GlutenFree.Databricks.AdoNet.Thrift` | Opt-in Thrift transport (real sessions), built on the [Apache Arrow ADBC Databricks driver](https://www.nuget.org/packages/Apache.Arrow.Adbc.Drivers.Databricks) |

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
- The add-on carries heavier transitive dependencies (ApacheThrift and friends) — that's
  why it ships as a separate opt-in package rather than in the core provider.

The integration test suites can run against either transport: set
`DATABRICKS_TRANSPORT=thrift` alongside the usual `DATABRICKS_*` variables.

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
- **No multi-statement transactions** — Databricks SQL doesn't support them;
  `BeginTransaction` throws `NotSupportedException`. (The linq2db provider declares
  `TransactionsSupported=false` so linq2db never attempts one.)
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
