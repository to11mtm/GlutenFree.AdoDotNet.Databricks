# GlutenFree ADO.NET Provider for Databricks

An open source, from-scratch **ADO.NET data provider for Databricks SQL warehouses**, plus a
**linq2db provider** built on top of it. No Thrift, no ODBC driver installs — just HTTP against
the Databricks [Statement Execution API](https://docs.databricks.com/api/workspace/statementexecution)
with Apache Arrow result decoding.

| Package | Description |
|---|---|
| `GlutenFree.Databricks.AdoNet` | Core ADO.NET provider (`DbConnection`/`DbCommand`/`DbDataReader`) |
| `GlutenFree.Databricks.AdoNet.Linq2Db` | [linq2db](https://github.com/linq2db/linq2db) data provider |

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

## Connection string reference

| Keyword | Default | Description |
|---|---|---|
| `Host` | *(required)* | Workspace URL, e.g. `https://adb-123.azuredatabricks.net` |
| `WarehouseId` or `HttpPath` | *(required)* | SQL warehouse id, or its HTTP path (`/sql/1.0/warehouses/<id>`) |
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
| `DECIMAL(p>28,s)` | `System.Data.SqlTypes.SqlDecimal` (`GetSqlDecimal`, `GetString`) |
| `STRING` / `CHAR` | `string` |
| `BOOLEAN` | `bool` |
| `BINARY` | `byte[]` |
| `DATE` | `DateOnly` (`GetDateOnly`; `GetDateTime` also works) |
| `TIMESTAMP` / `TIMESTAMP_NTZ` | `DateTime` (UTC) |
| `ARRAY` / `MAP` / `STRUCT` / `VARIANT` / `INTERVAL` | `string` (JSON representation) |

## Limitations

- **SQL warehouses only** — all-purpose clusters would require the Thrift protocol
  (a transport abstraction exists for adding it later).
- **No multi-statement transactions** — Databricks SQL doesn't support them;
  `BeginTransaction` throws `NotSupportedException`. (The linq2db provider declares
  `TransactionsSupported=false` so linq2db never attempts one.)
- **Input parameters only** — no output/return parameters, no stored procedures.
- **`BINARY` parameters unsupported** by the Statement Execution API — pass hex/base64
  strings and decode in SQL.
- Connection pooling is a no-op: the REST transport is stateless HTTP (HTTP handlers and
  OAuth tokens are shared internally where safe).

## Testing

```powershell
dotnet test   # integration tests skip unless DATABRICKS_* env vars are set
```

Live integration tests run against a real warehouse (Databricks Free Edition works) —
see [planning/integration-test-setup.md](planning/integration-test-setup.md).

## License

[Apache License 2.0](LICENSE)
