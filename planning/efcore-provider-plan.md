# EF Core Provider Plan (future work)

Status: **Speculative / not started.** This document specs out what an EF Core
provider (`GlutenFree.EntityFrameworkCore.Databricks`) built on
`GlutenFree.Databricks.AdoNet` would involve, so the work can be picked up later
without re-deriving the research. Findings verified against `dotnet/efcore`
`release/9.0` (EFCore.Sqlite.Core as the minimal-relational-provider reference)
and `npgsql/efcore.pg` (full-featured third-party reference).

## 1. Why, and what shape

linq2db support already exists as an add-on package; EF Core is the other big
.NET data-access ecosystem. The provider would be a new add-on package,
following the existing pattern (core ADO.NET package + independent add-ons):

- **Package/assembly:** `GlutenFree.EntityFrameworkCore.Databricks` (matches
  the `Npgsql.EntityFrameworkCore.PostgreSQL` / `Pomelo.EntityFrameworkCore.MySql`
  convention: `<Org>.EntityFrameworkCore.<Db>`).
- **Targeting:** **EF Core 10 → `net10.0`** (EF 9 goes out of support too soon
  to be worth leading with). The add-on csproj overrides the repo-wide
  `net8.0` from `Directory.Build.props` with `<TargetFramework>net10.0</TargetFramework>`;
  the core `GlutenFree.Databricks.AdoNet` (net8.0) package is consumable from
  net10.0 as-is. Third-party providers track EF majors via release branches,
  not multi-TFM builds (npgsql precedent). Version the package to track the EF
  major (10.x for EF 10), documented in the README. CI needs the .NET 10 SDK.
  Research citations below were verified against `release/9.0`; re-verify the
  service list against `release/10.0` during Phase 0 (differences expected to
  be additive core services only, plus EF 10's `ParameterizedCollectionMode`).
- Reference `Microsoft.EntityFrameworkCore.Relational` with
  `PrivateAssets="none"` so the EF analyzer flows to users (npgsql does this).

## 2. The two hard problems

Everything else is boilerplate; these two decide the provider's character.

### 2.1 Transactions (transport-dependent)

Databricks *does* support multi-statement transactions
([docs](https://docs.databricks.com/aws/en/transactions/)), on Unity Catalog
managed Delta/Iceberg tables with catalog commits enabled. There are two modes,
and which one we get depends on the ADO.NET transport:

| Mode | Syntax | Our transport | EF impact |
|---|---|---|---|
| Interactive | `BEGIN TRANSACTION; … COMMIT;`/`ROLLBACK;` (stateful session) | **Thrift** | EF works unmodified — `RelationalConnection` gets a real `DbTransaction` |
| Non-interactive | `BEGIN ATOMIC … END;` (one submitted statement) | **REST** (and Thrift) | Per-batch atomicity via the modification command batch |

**Prerequisite (ADO.NET layer, being done first):** implement a real
`DatabricksTransaction : DbTransaction` driven by
`IDatabricksTransport.SupportsTransactions`. Thrift holds one
`AdbcConnection` = one server-side session, so `BEGIN TRANSACTION` works there;
REST is stateless and keeps throwing `NotSupportedException`.

**EF design that falls out of this:**

- **Thrift:** nothing special. Use the stock relational transaction plumbing;
  `BatchExecutor` begins/commits real transactions and `SaveChanges` is atomic.
  Register an `IRelationalTransactionFactory` only to report
  `SupportsSavepoints => false` (Databricks has no savepoints), which makes
  `BatchExecutor` skip the savepoint path.
- **REST:** the connection can't begin a transaction, so make each
  `ModificationCommandBatch` atomic instead — our `ReaderModificationCommandBatch`
  subclass wraps its statements in `BEGIN ATOMIC … END;`. This maps naturally
  onto EF's batching model (a batch already *is* an ordered statement list).
  Caveats to handle in the batch's `Consume`: an ATOMIC block does not report
  per-statement rows-affected, so rows-affected verification and store-generated
  value propagation must be relaxed for wrapped batches (see §2.2 — we don't
  support store-generated values in v1 anyway).
  Multi-*batch* saves are still non-atomic; document that, and document
  `AutoTransactionBehavior.Never` plus "use the Thrift transport for real
  transactions" as the escape hatches.
- **No stub transactions.** The earlier InMemory-style
  warn-and-stub design is dropped; where we genuinely cannot provide atomicity
  (REST + explicit `Database.BeginTransaction()`), we surface the ADO.NET
  `NotSupportedException` rather than silently pretending.

Databricks-specific behavior to document either way: snapshot isolation with
optimistic concurrency (conflicts surface at commit → apps need retry logic);
interactive transactions detect conflicts at table level while `BEGIN ATOMIC` is
row level; no metadata/DDL operations inside interactive transactions (so
`EnsureCreated`/migrations must run outside one); one transaction at a time per
connection; target tables must be UC-managed with catalog commits enabled.

### 2.2 Keys, identity, and concurrency semantics

Delta tables don't enforce PK/FK/UNIQUE constraints (they're informational),
and `GENERATED ALWAYS AS IDENTITY` columns exist but there is no
`RETURNING`/`OUTPUT`-style retrieval usable from plain INSERT in all cases.

- Model conventions should default keys to `ValueGenerated.Never` (client
  supplies keys — GUIDs/BIGINT), with opt-in identity via an annotation if we
  later implement generated-key retrieval (e.g. `INSERT ... ; SELECT max()` is
  unsafe — better to just not support store-generated keys in v1).
- No database-enforced uniqueness ⇒ document that EF's identity-map assumption
  "PK is unique" is on the app.
- Concurrency tokens: no rowversion; `UPDATE ... WHERE token = old` works
  (affected-rows check) — supportable later, not v1.

## 3. Service registrations (`AddEntityFrameworkDatabricks`)

Per `SqliteServiceCollectionExtensions.AddEntityFrameworkSqlite` (verified on
EF 9; re-verify on `release/10.0` in Phase 0), using
`EntityFrameworkRelationalServicesBuilder` + `TryAddCoreServices()` last.

**Mandatory (no usable relational default):**

| Service | Our implementation | Notes |
|---|---|---|
| `LoggingDefinitions` | `DatabricksLoggingDefinitions` | subclass `RelationalLoggingDefinitions` |
| `IDatabaseProvider` | `DatabaseProvider<DatabricksOptionsExtension>` | what makes EF recognize the provider |
| `IRelationalTypeMappingSource` | `DatabricksTypeMappingSource` | see §5; builds on our `DatabricksTypeMap` |
| `IRelationalConnection` | `DatabricksRelationalConnection` | wraps `DatabricksConnection`; real transactions on Thrift (§2.1) |
| `IUpdateSqlGenerator` | `DatabricksUpdateSqlGenerator` | `requiresTransaction: false` for singles |
| `IModificationCommandBatchFactory` | `DatabricksModificationCommandBatchFactory` | produces `BEGIN ATOMIC`-wrapped batches when the connection has no transaction support (§2.1) |
| `IRelationalTransactionFactory` | `DatabricksTransactionFactory` | only to report `SupportsSavepoints => false` |
| `IProviderConventionSetBuilder` | `DatabricksConventionSetBuilder` | base is abstract; key conventions (§2.2) |
| `IRelationalDatabaseCreator` | `DatabricksDatabaseCreator` | §6 |
| `IHistoryRepository` | `DatabricksHistoryRepository` | required to register even if migrations throw |

**Customized (defaults exist, but the dialect needs them):**

| Service | Why |
|---|---|
| `ISqlGenerationHelper` | backtick quoting, `:name` parameter markers (match ADO.NET layer & linq2db builder) |
| `IQuerySqlGeneratorFactory` | LIMIT/OFFSET, no APPLY → `LATERAL` joins, dialect quirks |
| `IMethodCallTranslatorProvider` / `IMemberTranslatorProvider` / `IAggregateMethodCallTranslatorProvider` | string/date/math → Databricks SQL functions (port knowledge from `DatabricksMemberTranslator` in the linq2db package) |
| `ISqlExpressionFactory` | if we need typed expression conveniences |
| `IQueryableMethodTranslatingExpressionVisitorFactory` / `IRelationalSqlTranslatingExpressionVisitorFactory` / `IQueryTranslationPostprocessorFactory` | only as quirks surface (e.g. APPLY→LATERAL rewrites) |
| `IModelValidator` | reject unsupported model features early (store-generated keys, rowversion, schemas outside catalog.schema, etc.) |
| `IMigrationsSqlGenerator` | supported-DDL subset (§6) |
| `IRelationalParameterBasedSqlProcessorFactory` | if parameter inlining/collection-parameter handling needs Databricks behavior |

Everything else comes from `TryAddCoreServices()`. Respect enforced lifetimes
(TypeMappingSource/SqlGenerationHelper/UpdateSqlGenerator are Singleton;
RelationalConnection/BatchFactory/MigrationsSqlGenerator are Scoped).

## 4. Options plumbing (`UseDatabricks`)

Standard three-class pattern (SQLite reference):

1. `DatabricksOptionsExtension : RelationalOptionsExtension` — immutable
   clone-on-`With*`, `ApplyServices => AddEntityFrameworkDatabricks()`, nested
   `ExtensionInfo : RelationalExtensionInfo` (`IsDatabaseProvider => true`,
   `LogFragment`, `PopulateDebugInfo`). Databricks-specific knobs (npgsql's
   `NpgsqlOptionsExtension` is the model): default catalog/schema, warehouse
   settings, and later a "server version"-style toggle if warehouse channels
   diverge. `Validate()` cross-checks incompatible options.
2. `DatabricksDbContextOptionsBuilder :
   RelationalDbContextOptionsBuilder<DatabricksDbContextOptionsBuilder, DatabricksOptionsExtension>`
   — gets `CommandTimeout`/`MaxBatchSize`/`ExecutionStrategy` for free.
3. `UseDatabricks(...)` extensions: (connectionString), (DbConnection),
   (DbConnection, contextOwnsConnection) + `ConfigureWarnings` defaults.

## 5. Type mapping

`DatabricksTypeMappingSource : RelationalTypeMappingSource`, aligned with the
existing `DatabricksTypeMap` (ADO.NET) and `DatabricksMappingSchema` (linq2db):

- BIGINT/INT/SMALLINT/TINYINT, DOUBLE/FLOAT, DECIMAL(p,s) (incl. the
  precision>28 → `DatabricksDecimal`/`SqlDecimal` story — likely *not* mapped
  by default in EF; document), STRING (no length facets), BOOLEAN, DATE,
  TIMESTAMP / TIMESTAMP_NTZ (DateTimeOffset/DateTime decision must match the
  reader), BINARY, and later ARRAY/MAP/STRUCT (out of scope v1; EF 8+
  primitive collections could map to ARRAY eventually).
- Literal generation must match the SQL builder rules already proven in
  linq2db (string escaping, timestamp literals, hex binary).

## 6. Migrations & database creation (deliberately minimal in v1)

- `DatabricksDatabaseCreator : RelationalDatabaseCreator`: `Exists` = catalog/
  schema visible via `information_schema`; `HasTables` = query
  `information_schema.tables`; `Create`/`Delete` = `CREATE/DROP SCHEMA`.
  This makes **`EnsureCreated`/`EnsureDeleted` the v1 story** (it uses the
  migrations SQL generator's CREATE TABLE path on the model).
- `DatabricksMigrationsSqlGenerator : MigrationsSqlGenerator`: implement the
  Delta-supported subset — CreateTable (Delta types, `USING DELTA`, comments,
  informational PK), DropTable, AddColumn, RenameColumn/Table, DropColumn,
  InsertData/DeleteData/UpdateData — and let the base throw for the rest
  (no SQLite-style table-rebuild machinery in v1).
- `DatabricksHistoryRepository`: Delta table `__EFMigrationsHistory` in the
  target schema; workable since it's plain INSERT/DELETE/SELECT. Full
  `Migrate()` support can therefore come cheaply after `EnsureCreated` works.

## 7. Testing

- Unit tests (`tests/...EntityFrameworkCore.Tests`): SQL generation snapshots
  via `context.Set<T>().Where(...).ToQueryString()`, update SQL generation,
  type-mapping round trips, options/extension plumbing. No warehouse needed.
- Integration tests (`tests/...EntityFrameworkCore.IntegrationTests`):
  follow the existing `IntegrationConfig` pattern; CRUD + query translation
  against a real warehouse; re-run over both REST and Thrift transports via
  the established subclass-rerun pattern.
- EF spec tests: package `Microsoft.EntityFrameworkCore.Relational.Specification.Tests`
  ships abstract xunit suites + `RelationalTestStore` infra. Full-suite runs
  (npgsql-style) assume transactions/migrations/Northwind seeding — **start
  with a curated subset**: `NorthwindQueryRelationalTestBase`-family (query
  correctness) with a `DatabricksTestStore` that seeds Northwind via batched
  INSERTs; skip the migrations suites, and run `TransactionTestBase` only on
  the Thrift transport (and only against catalog-managed tables). Warehouse
  latency makes even the query suites slow — likely a manually-triggered CI job.

## 8. Phasing

**Phase −1 (prerequisite, in the core ADO.NET package):** real
`DatabricksTransaction` support — `IDatabricksTransport.SupportsTransactions`,
`BEGIN TRANSACTION`/`COMMIT`/`ROLLBACK` on session-capable transports (Thrift),
`NotSupportedException` on REST. See §2.1.

1. **Phase 0 — spike:** package skeleton, options plumbing, mandatory
   services with mostly-default implementations; get `ToQueryString()` and a
   simple `ToListAsync()` working against a warehouse.
2. **Phase 1 — query provider (the real value):** type mappings, SQL
   generation quirks (backticks, LIMIT/OFFSET, LATERAL), function translators
   (port linq2db translator knowledge), curated Northwind spec-test subset.
   Read-only EF is already useful for a lakehouse.
3. **Phase 2 — SaveChanges:** update SQL generator, `BEGIN ATOMIC`-wrapping
   batch factory, transaction factory (no savepoints), conventions for
   client-generated keys, model validator.
4. **Phase 3 — EnsureCreated + minimal migrations:** database creator,
   CREATE TABLE generation, history repository; then evaluate full `Migrate()`.
5. **Later / out of scope v1:** scaffolding (`IDatabaseModelFactory` /
   `IProviderCodeGenerator` in a Design sub-package), ARRAY/MAP/STRUCT,
   primitive collections → ARRAY, MERGE-based `ExecuteUpdate` optimizations,
   store-generated identity keys, concurrency tokens, retrying execution
   strategy tuned to warehouse cold starts.

## 9. Open questions

- ~~Rollback behavior on the stub transaction~~ — moot: transactions are real
  on Thrift, and REST surfaces `NotSupportedException` (no stubs). See §2.1.
- Store-generated keys: skip entirely in v1 (recommended) or attempt
  identity-column retrieval?
   - Attempt Identity Column retrieval if the warehouse supports it (e.g. `INSERT ... ; SELECT max()` is unsafe — better to just not support store-generated keys in v1).
- Spec-test subset in CI: which suites, and manual vs. scheduled runs given
  warehouse cost/latency?
  - Start with a curated subset: `NorthwindQueryRelationalTestBase`-family (query correctness) with a `DatabricksTestStore` that seeds Northwind via batched INSERTs; skip `TransactionTestBase` and migrations suites. Warehouse latency makes even the query suites slow — likely a manually-triggered CI job.
