# Databricks ADO.NET Provider — Requirements

Open source ADO.NET provider for Databricks SQL (`Databricks.AdoNet`).

## Key decisions

| Decision | Choice |
|---|---|
| Wire protocol | Databricks **REST Statement Execution API** (`/api/2.0/sql/statements`) first, behind an `IDatabricksTransport` abstraction so a Thrift/HiveServer2 transport can be added later |
| Target framework | **net8.0** only; modern APIs (`Span<T>`, `IAsyncEnumerable`, `System.Text.Json` source-gen, etc.) |
| Result format | **ARROW_STREAM** via `Apache.Arrow` package (primary); `JSON_ARRAY` fallback |
| Naming | Package/namespace `Databricks.AdoNet`; classes prefixed `Databricks*` (`DatabricksConnection`, `DatabricksCommand`, …) |
| Compute targets | SQL Warehouses (REST API requirement). All-purpose clusters only once Thrift transport exists |

## 1. Connection & configuration

- `DatabricksConnectionStringBuilder` (derives `DbConnectionStringBuilder`) with strongly-typed properties.
- Connection string keywords (case-insensitive):
  - `Host` — workspace URL (e.g. `https://adb-123.azuredatabricks.net`)
  - `HttpPath` or `WarehouseId` — SQL warehouse identifier
  - `AuthType` — `Pat` (default) | `OAuthM2M`
  - `Token` — personal access token (when `AuthType=Pat`)
  - `ClientId` / `ClientSecret` — OAuth M2M (service principal) client credentials
  - `Catalog`, `Schema` — initial namespace
  - `CommandTimeout` (seconds, default 0 = server default), `ConnectTimeout`
  - `ResultFormat` — `Arrow` (default) | `Json`
  - `Disposition` — `Auto` (default; server picks inline vs external links) | `Inline` | `ExternalLinks`
  - `MaxRetries`, `RetryBaseDelay` — retry policy for 429/503
  - `SessionParameters` passthrough (e.g. `ANSI_MODE`) — stretch
- Validation with clear error messages for missing/conflicting keywords.
- `ChangeDatabase(name)` maps to changing default catalog/schema (`USE` semantics).

## 2. Authentication

- **PAT**: `Bearer` header from `Token`.
- **OAuth M2M** (client credentials against workspace `/oidc/v1/token`, scope `all-apis`): automatic token acquisition, caching, and refresh before expiry; thread-safe.
- Extensible `IDatabricksAuthenticator` abstraction so U2M/browser-based and Azure AD flows can be added later without breaking changes.
- Never log or expose secrets; `DatabricksConnectionStringBuilder.ToString()` must be able to redact secrets; secrets excluded from exception messages.

## 3. Command execution

- `DatabricksCommand : DbCommand` supporting `CommandType.Text` (StoredProcedure not supported → `NotSupportedException`).
- Sync and async: `ExecuteReader(Async)`, `ExecuteNonQuery(Async)`, `ExecuteScalar(Async)`; sync implemented over async without deadlocks.
- Statement lifecycle against REST API:
  - Submit with hybrid `wait_timeout` polling: initial synchronous wait, then poll `GET /statements/{id}` with backoff.
  - Honor `CommandTimeout`; on timeout or `Cancel()`/`CancellationToken`, issue `POST /statements/{id}/cancel`.
- `ExecuteNonQuery` returns affected row count when Databricks reports it (`num_affected_rows`), else -1.

## 4. Parameters

- `DatabricksParameter : DbParameter`, `DatabricksParameterCollection : DbParameterCollection`.
- Named markers (`:name`) mapped to the REST API's native `parameters` field (server-side binding — no client-side string substitution, inherently injection-safe).
- .NET → Databricks type inference for parameter values (with explicit `DbType`/`DatabricksType` override).
- Input parameters only (API limitation); Output/Return directions throw `NotSupportedException`.

## 5. Results & type mapping

- `DatabricksDataReader : DbDataReader`:
  - Streams `ARROW_STREAM` batches (inline or via external links / presigned URLs) using `Apache.Arrow`; downloads external chunks lazily/sequentially with prefetch of the next chunk.
  - `JSON_ARRAY` fallback path for `ResultFormat=Json`.
  - `GetSchemaTable()` and `GetColumnSchema()` from result manifest.
  - Standard typed getters plus `GetFieldValue<T>`; `IAsyncEnumerable`-friendly (`ReadAsync`).
- Type mapping (Databricks → .NET): `TINYINT→sbyte`, `SMALLINT→short`, `INT→int`, `BIGINT→long`, `FLOAT→float`, `DOUBLE→double`, `DECIMAL(p,s)→decimal`, `STRING→string`, `BOOLEAN→bool`, `BINARY→byte[]`, `DATE→DateOnly`, `TIMESTAMP/TIMESTAMP_NTZ→DateTime` (offset semantics documented), `INTERVAL→string` (v1), `ARRAY/MAP/STRUCT→string` (JSON representation, v1; typed access is stretch), `VARIANT→string`.
- `DBNull.Value` for SQL NULLs everywhere.

### Arrow (.NET) risk assessment — apache/arrow-dotnet

Reviewed arrow-dotnet feature matrix vs Databricks output; coverage is sufficient. Notes:

- **All Databricks-emitted Arrow types are implemented** in C#: ints, float/double, decimal (Decimal128), string, binary, Date32, Timestamp, List/Struct/Map, Interval, Duration.
- **LZ4 compression**: Databricks may LZ4_FRAME-compress Arrow batches (external links / cloud fetch). Must reference `Apache.Arrow.Compression` and pass its `CompressionCodecFactory` to `ArrowStreamReader`.
- **High-precision DECIMAL / INT128**: Arrow format has no INT128 array type (any language) — its 128-bit type is `Decimal128`, which arrow-dotnet implements. Databricks' widest integer is `BIGINT` (int64), so no Int128 array is ever needed. `DECIMAL(p>28)` exceeds .NET `decimal` range but not Arrow's: `Decimal128Array` exposes `GetSqlDecimal()` (full 38-digit) and `GetString()`; reader must surface these instead of overflowing. **Decision:** standardize on `SqlDecimal` (in-box, exactly 38-digit — matches Databricks' DECIMAL max) + `GetString()` for v1; a BigDecimal-style `DatabricksDecimal` struct is a backlog/stretch item. Optional nicety: `GetFieldValue<Int128>()` for `DECIMAL(38,0)` by reading the raw 16-byte value buffer. Not an Arrow limitation — JSON has the same `decimal` mapping problem.
- **Complex types**: the REST Statement Execution API returns `ARRAY`/`MAP`/`STRUCT` as JSON **strings even in Arrow format**, so arrow-dotnet's nested-type support isn't load-bearing for v1.
- **Not implemented in arrow-dotnet** (Large arrays >2 GiB, Views, Run-End Encoding, Tensors): not produced by Databricks statement results; chunk sizes are server-capped.
- **Conclusion**: no per-type JSON fallback needed; connection-level `ResultFormat=Json` remains the escape hatch if a server-side format change ever breaks Arrow decoding.

## 6. Transactions

- Databricks SQL has **no multi-statement transactions**. `BeginTransaction` throws `NotSupportedException` with a clear message (documented). Revisit if/when Databricks adds support.

## 7. Pooling

- REST transport is stateless HTTP → no physical connection to pool. Provider must:
  - Share/pool `HttpClient` handlers correctly (via `SocketsHttpHandler`, one per unique endpoint config, DNS-rotation friendly).
  - Cache OAuth tokens across connection instances with the same credentials.
  - Accept-and-ignore `Pooling=true/false` keyword for connection-string compatibility.
- Real session pooling becomes meaningful with the Thrift transport (future).

## 8. Errors, diagnostics, logging

- `DatabricksException : DbException` carrying `StatusCode`, Databricks `ErrorCode`, `SqlState`, and statement ID.
- Retry with exponential backoff + jitter on 429/503 (honoring `Retry-After`); only for idempotent phases (submit retries documented carefully).
- `Microsoft.Extensions.Logging.Abstractions` integration (`DatabricksConnection.LoggerFactory` or DI-friendly hook); no logging dependency forced on consumers.
- `System.Diagnostics.Activity` (OpenTelemetry-compatible) spans for statement execution — stretch.

## 9. Provider infrastructure

- `DatabricksProviderFactory : DbProviderFactory` + `DbProviderFactories` registration support.
- `DatabricksConnection.GetSchema()` for common collections (`Tables`, `Columns`, `Views`, `Catalogs`, `Schemas`) via information_schema — v1 minimal set.
- `DbBatch` support — stretch (API executes one statement per request; emulate sequentially).

## 10. Quality & delivery

- Unit tests (xUnit) with mocked HTTP transport; no network needed.
- Integration tests gated on env vars (`DATABRICKS_HOST`, `DATABRICKS_TOKEN`, `DATABRICKS_WAREHOUSE_ID`), skipped otherwise.
- Dapper compatibility smoke test (works out of the box with a compliant provider).
- CI (GitHub Actions): build, test, pack.
- NuGet packaging: SourceLink, symbols, deterministic build, XML docs, README.
- License: MIT (or Apache-2.0 — TBD before first publish).

## Non-goals (v1)

- Thrift/HiveServer2 transport, all-purpose cluster support
- Multi-statement transactions
- OAuth U2M (interactive browser) flow
- EF Core provider (separate project later). A **linq2db provider** (`Databricks.AdoNet.Linq2Db`)
  **is** in scope for the initial release — see `planning/linq2db-dataprovider.md` for reference
  material; it is far simpler than EF Core (DataProvider + SqlBuilder + SqlOptimizer +
  MappingSchema + SchemaProvider, modeled on linq2db's SQLite provider).
- Bulk copy / staging ingest APIs