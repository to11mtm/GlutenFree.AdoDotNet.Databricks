# EF Core Provider Plan

Status: **In progress.** Phase −1 and Phase 0 are complete; Phase 1 (the query
provider) is underway. See [§8 Phases and tracking](#8-phases-and-tracking) for
the current state of each work item.

This document specs out the EF Core provider
(`GlutenFree.EntityFrameworkCore.Databricks`) built on
`GlutenFree.Databricks.AdoNet`. Findings were verified against `dotnet/efcore`
(EFCore.Sqlite.Core as the minimal-relational-provider reference, `release/9.0`
for the original research and `release/10.0` while implementing) and
`npgsql/efcore.pg` (full-featured third-party reference).

## 1. Why, and what shape

linq2db support already exists as an add-on package; EF Core is the other big
.NET data-access ecosystem. The provider is a new add-on package, following the
existing pattern (core ADO.NET package + independent add-ons):

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
- Reference `Microsoft.EntityFrameworkCore.Relational` with
  `PrivateAssets="none"` so the EF analyzer flows to users (npgsql does this).
- **`IsPackable=false` until Phase 2 lands** — the package should not ship while
  `SaveChanges` is incomplete.

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

Per `SqliteServiceCollectionExtensions.AddEntityFrameworkSqlite`, using
`EntityFrameworkRelationalServicesBuilder` + `TryAddCoreServices()` last.

**Mandatory (no usable relational default):**

| Service | Our implementation | Status | Notes |
|---|---|---|---|
| `LoggingDefinitions` | `DatabricksLoggingDefinitions` | done | subclass `RelationalLoggingDefinitions` |
| `IDatabaseProvider` | `DatabaseProvider<DatabricksOptionsExtension>` | done | what makes EF recognize the provider |
| `IRelationalTypeMappingSource` | `DatabricksTypeMappingSource` | done | see §5; aligned with our `DatabricksTypeMap` |
| `IRelationalConnection` | `DatabricksRelationalConnection` | done | wraps `DatabricksConnection`; real transactions on Thrift (§2.1) |
| `IUpdateSqlGenerator` | `DatabricksUpdateSqlGenerator` | stub (Phase 2) | relational base is sufficient for plain DML |
| `IModificationCommandBatchFactory` | `DatabricksModificationCommandBatchFactory` | one statement per batch (Phase 2 → `BEGIN ATOMIC`) | see §2.1 |
| `IRelationalTransactionFactory` | `DatabricksTransactionFactory` | done | reports `SupportsSavepoints => false` |
| `IProviderConventionSetBuilder` | `DatabricksConventionSetBuilder` | pass-through (Phase 2) | base is abstract; key conventions still to add (§2.2) |
| `IRelationalDatabaseCreator` | `DatabricksDatabaseCreator` | done | `CREATE`/`DROP SCHEMA`, `information_schema` probes (§6) |
| `IHistoryRepository` | `DatabricksHistoryRepository` | **not registered** (Phase 3) | until then `Migrate()` fails with a DI error rather than a clear message |

**Customized (defaults exist, but the dialect needs them):**

| Service | Status | Why |
|---|---|---|
| `ISqlGenerationHelper` | done | backtick quoting, `:name` parameter markers (match ADO.NET layer & linq2db builder) |
| `IQuerySqlGeneratorFactory` | partial | `LIMIT`/`OFFSET` + `CAST(COUNT AS INT)` done; `APPLY` → `LATERAL` still to do |
| `IMethodCallTranslatorProvider` / `IMemberTranslatorProvider` | partial | string/date/math functions started; see Phase 1 |
| `IAggregateMethodCallTranslatorProvider` | not needed so far | the `COUNT` narrowing is handled at render time in the query SQL generator, which keeps `DISTINCT`/predicate/selector semantics from the shared translator |
| `ISqlExpressionFactory` | not needed yet | only if we need typed expression conveniences |
| `IQueryableMethodTranslatingExpressionVisitorFactory` / `IRelationalSqlTranslatingExpressionVisitorFactory` / `IQueryTranslationPostprocessorFactory` | not needed yet | add as quirks surface (e.g. APPLY→LATERAL rewrites) |
| `IModelValidator` | Phase 2 | reject unsupported model features early (store-generated keys, rowversion, etc.) |
| `IMigrationsSqlGenerator` | Phase 3 | supported-DDL subset (§6) |
| `IRelationalParameterBasedSqlProcessorFactory` | not needed yet | only if parameter inlining/collection-parameter handling needs Databricks behavior |

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

- **Done:** `DatabricksDatabaseCreator : RelationalDatabaseCreator`. An EF
  "database" is a Unity Catalog *schema* (the catalog is provisioned out of
  band), so `Create`/`Delete` issue `CREATE SCHEMA` / `DROP SCHEMA … CASCADE`,
  and `Exists`/`HasTables` query the catalog-qualified `information_schema`.
- **Phase 3:** `DatabricksMigrationsSqlGenerator : MigrationsSqlGenerator`
  implementing the Delta-supported subset — CreateTable (Delta types,
  `USING DELTA`, comments, informational PK), DropTable, AddColumn,
  RenameColumn/Table, DropColumn, InsertData/DeleteData/UpdateData — letting the
  base throw for the rest (no SQLite-style table-rebuild machinery in v1).
  This is what makes `EnsureCreated` able to create tables.
- **Phase 3:** `DatabricksHistoryRepository` — a Delta `__EFMigrationsHistory`
  table in the target schema; workable since it is plain INSERT/DELETE/SELECT.
  Full `Migrate()` support can therefore come cheaply after `EnsureCreated`.
  Until it is registered, `Migrate()` fails with a DI resolution error rather
  than a clear "not supported yet" message.
- Note: DDL cannot run inside an interactive transaction, so `EnsureCreated` and
  migrations must not be wrapped in one (§2.1).

## 7. Testing

- **Done — unit tests** (`tests/GlutenFree.EntityFrameworkCore.Databricks.Tests`):
  exact-SQL assertions via `ToQueryString()`, SQL-generation-helper escaping,
  options/extension plumbing, and targeting guards. No warehouse needed.
- **Done — integration tests**
  (`tests/GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests`): follows
  the existing `IntegrationConfig`/`IntegrationFact` pattern; covers
  materialization, parameterized predicates, paging, aggregates and string
  translation against a real warehouse.
- **Phase 1 — re-run over Thrift:** the ADO.NET and linq2db suites re-run
  themselves over the Thrift transport via a module initializer plus subclasses;
  the EF suite should do the same.
- **Phase 1 — EF spec tests:** package
  `Microsoft.EntityFrameworkCore.Relational.Specification.Tests` ships abstract
  xunit suites + `RelationalTestStore` infra. Full-suite runs (npgsql-style)
  assume transactions/migrations/Northwind seeding — **start with a curated
  subset**: `NorthwindQueryRelationalTestBase`-family (query correctness) with a
  `DatabricksTestStore` that seeds Northwind via batched INSERTs; skip the
  migrations suites, and run `TransactionTestBase` only on the Thrift transport
  (and only against catalog-managed tables). Warehouse latency makes even the
  query suites slow — likely a manually-triggered CI job.

Note for whoever runs the whole solution with credentials configured: the
integration suites share fixed Delta tables, and Delta uses optimistic
concurrency, so test modules must run sequentially
(`dotnet test --max-parallel-test-modules 1`, as CI does).

## 8. Phases and tracking

Legend: **[x]** done · **[~]** partially done · **[ ]** not started.
Each phase lists its exit criteria; a phase is not "done" until those hold.

### Phase −1 — ADO.NET transactions (prerequisite) — **DONE**

In the core `GlutenFree.Databricks.AdoNet` package, not the EF provider.

- [x] `IDatabricksTransport.SupportsTransactions` (DIM defaulting to `false`);
      `true` for Thrift, `false` for REST
- [x] `DatabricksTransaction : DbTransaction` — `BEGIN TRANSACTION`/`COMMIT`/
      `ROLLBACK`, snapshot isolation reported, no savepoints, dispose rolls back
- [x] `DatabricksConnection.BeginDbTransaction`/`CurrentTransaction`; close
      abandons; `ChangeDatabase`/`ChangeCatalog` blocked mid-transaction
- [x] `DatabricksCommand.Transaction` validates ownership instead of throwing
- [x] Unit tests over a fake session transport; live Thrift integration tests
      (commit, rollback, dispose-rolls-back, cross-connection invisibility)

**Exit criteria (met):** commit/rollback verified against a live warehouse.

### Phase 0 — provider skeleton and first query — **DONE**

- [x] Project `src/GlutenFree.EntityFrameworkCore.Databricks` (net10.0, EF 10,
      `PrivateAssets="none"`, `IsPackable=false`) + unit and integration test projects
- [x] Options plumbing: `DatabricksOptionsExtension`,
      `DatabricksDbContextOptionsBuilder`, `UseDatabricks` overloads
      (connection string / `DbConnection` / owned `DbConnection`, generic forms),
      `UseCatalog`/`UseSchema`
- [x] `AddEntityFrameworkDatabricks` service registration (§3)
- [x] `DatabricksRelationalConnection` (catalog/schema overrides applied to the
      connection string, since `ChangeCatalog` requires an open connection)
- [x] `DatabricksSqlGenerationHelper` — backticks with `` ` `` doubling, `:name`
- [x] `DatabricksTypeMappingSource` + `DatabricksBoolTypeMapping` (§5)
- [x] `DatabricksQuerySqlGenerator` — `LIMIT`/`OFFSET`
- [x] `DatabricksUpdateSqlGenerator`, `DatabricksModificationCommandBatchFactory`
      (one statement per batch), `DatabricksTransactionFactory`
- [x] `DatabricksDatabaseCreator`
- [x] Tests: exact-SQL generation, SQL-helper escaping, options plumbing,
      targeting guards; live integration tests

**Exit criteria (met):** `ToQueryString()` and `ToListAsync()` work against a
live warehouse.

### Phase 1 — query provider — **IN PROGRESS**

The read-only story, which is already the bulk of the value for a lakehouse.

Done so far:

- [x] `LIMIT ALL` for an unbounded `Skip` (a `BIGINT` bound is rejected)
- [x] `CAST(COUNT(...) AS INT)` narrowing, done at render time so `DISTINCT`,
      predicates and selectors keep the shared translator's semantics
- [x] **String concatenation → `||`.** Spark's `+` is arithmetic only: applied to
      strings it coerces operands to numbers and yields `NULL`, so EF's default
      `+` was silently producing wrong results.
- [x] **Spark string-literal escaping.** EF escapes an embedded quote by doubling
      it; Spark reads `''` as two adjacent literals and *drops* the quote, so
      `DatabricksStringTypeMapping`/`DatabricksCharTypeMapping` use backslash
      escaping (matching the linq2db `DatabricksMappingSchema`).
- [x] `DatabricksStringMethodTranslator` — `StartsWith`/`EndsWith`/`Contains`
      (native `startswith`/`endswith`/`contains`), `ToUpper`/`ToLower`,
      `Trim`/`TrimStart`/`TrimEnd`, `Replace`, `IndexOf` (`locate` − 1),
      `Substring` (1-based), `IsNullOrEmpty`/`IsNullOrWhiteSpace`,
      `PadLeft`/`PadRight` (`lpad`/`rpad`), static `string.Concat`
- [x] `DatabricksStringMemberTranslator` — `string.Length` → `length`
- [x] `DatabricksDateTimeMemberTranslator` — `Year`/`Month`/`Day`/`Hour`/
      `Minute`/`Second`/`DayOfYear`, `DayOfWeek` (offset by 1), `Date`
      (`date_trunc`), `Now`/`UtcNow`/`Today`
- [x] `DatabricksDateTimeMethodTranslator` — `AddYears`/`AddMonths`/`AddDays`/
      `AddHours`/`AddMinutes`/`AddSeconds`/`AddMilliseconds` via
      `timestampadd`; fractional amounts decline translation rather than
      truncating silently
- [x] `DatabricksMathTranslator` — `Abs`, `Ceiling`, `Floor`, `Round`, `Pow`,
      `Sqrt`, `Exp`, `Log`/`Log10`, trig, `Sign`, `Truncate`
- [x] `char` mapped to a one-character `STRING` via `CharToStringConverter`
- [x] **`APPLY` → `LATERAL`.** EF 10 rewrites correlated shapes into
      `ROW_NUMBER() OVER (PARTITION BY …)` subqueries and never emits `APPLY`
      for the common navigations, so this is now *defensive*:
      `VisitCrossApply`/`VisitOuterApply` emit
      `INNER JOIN LATERAL … ON TRUE` / `LEFT JOIN LATERAL … ON TRUE` in case a
      shape ever does produce one.
- [x] Set operations (`Union`/`Except`), `Distinct` + `OrderBy`, `IN` lists and
      `EF.Functions.Like` verified against the server — all use the relational
      defaults unchanged
- [x] `ExecuteUpdate`/`ExecuteDelete` — work on Delta tables with the stock
      translation, including referencing the existing column value
- [x] `TIMESTAMP_NTZ` and `DECIMAL` round trips verified (wall-clock preserved,
      `DateTimeKind.Unspecified`, scale intact)
- [x] Integration suites re-run over the Thrift transport
      (`GlutenFree.EntityFrameworkCore.Databricks.Thrift.IntegrationTests`),
      using the established module-initializer + subclass pattern

Remaining:

- [ ] **`decimal` beyond .NET precision** — the ADO.NET layer surfaces
      `SqlDecimal` above precision 28 and EF has no mapping for it. Decide:
      document only, reject in the model validator, or add a value converter.
      (See §9.)
- [ ] **Curated spec-test subset** — `NorthwindQueryRelationalTestBase`-family
      with a `DatabricksTestStore` (§7). This is the large remaining item.
- [ ] **`GroupBy` beyond simple aggregates** — `Having`, multiple keys and
      grouping by a computed expression are untested against the server.
- [ ] **Nullability/`??` semantics** — Spark's `NULL` handling in comparisons and
      `COALESCE` should be spot-checked against EF's expectations.

**Exit criteria:** the curated Northwind query suite passes against a live
warehouse over both transports, and no common LINQ shape silently falls back to
client evaluation in a `WHERE` clause.

### Phase 2 — `SaveChanges` — **NOT STARTED**

- [ ] `BEGIN ATOMIC … END;`-wrapping `ReaderModificationCommandBatch` for the
      REST transport, with rows-affected verification relaxed for wrapped
      batches (§2.1)
- [ ] Keep the current one-statement-per-batch behavior on Thrift, where real
      transactions already provide atomicity
- [ ] `DatabricksConventionSetBuilder`: default keys to `ValueGenerated.Never`
      (§2.2)
- [ ] `DatabricksModelValidator`: reject store-generated keys, rowversion
      concurrency tokens, and other unsupported model shapes with clear messages
- [ ] Optimistic-concurrency tokens via `UPDATE … WHERE token = old` +
      affected-rows check (decide in/out for v1)
- [ ] Live integration tests: insert/update/delete, multi-entity saves, and the
      atomicity difference between transports

**Exit criteria:** CRUD round-trips against a live warehouse on both transports,
with the atomicity guarantees documented per transport.

### Phase 3 — `EnsureCreated` and minimal migrations — **NOT STARTED**

- [ ] `DatabricksMigrationsSqlGenerator`: the Delta-supported DDL subset (§6)
- [ ] `DatabricksHistoryRepository` (also fixes the current DI error when
      `Migrate()` is called)
- [ ] Verify `EnsureCreated`/`EnsureDeleted` end to end
- [ ] Decide whether full `Migrate()` is in scope for v1

**Exit criteria:** `EnsureCreated` builds the model's tables on a real warehouse.

### Later / out of scope for v1

- [ ] Scaffolding (`IDatabaseModelFactory` / `IProviderCodeGenerator` in a
      Design sub-package)
- [ ] `ARRAY`/`MAP`/`STRUCT` and primitive collections → `ARRAY`
- [ ] MERGE-based upsert optimizations
- [ ] Store-generated identity keys
- [ ] Retrying execution strategy tuned to warehouse cold starts

### Behaviors found only by running against a live warehouse

Worth keeping in mind when extending the provider — none of these surface in
offline SQL-generation tests:

- **`LIMIT` must be an `INT` expression.** `OFFSET` requires a `LIMIT`, and a
  `BIGINT` bound is rejected with `INVALID_LIMIT_LIKE_EXPRESSION.DATA_TYPE`, so
  an unbounded skip emits `LIMIT ALL`.
- **`COUNT` returns `BIGINT`.** EF materializes `Count` as `int` and the reader
  will not narrow, so the generator wraps it in `CAST(... AS INT)`.
- **The relational base translates almost nothing.**
  `RelationalMemberTranslatorProvider` ships with an empty translator list, and
  the method-call base does not cover `StartsWith`/`Contains`, so a provider
  must supply them or every such predicate fails to translate.
- **Aggregate selectors are not `SqlExpression`s.** For `g.Count()` the
  `EnumerableExpression.Selector` is a `RelationalStructuralTypeShaperExpression`,
  so guarding on `Selector is SqlExpression` silently disables a translator.
- **`+` does not concatenate strings.** Spark's `+` is arithmetic: applied to
  strings it coerces the operands to numbers and yields `NULL`. Databricks
  spells concatenation `||`. EF's default operator map is therefore wrong here,
  and wrong *silently* — the query succeeds and returns nulls.
- **`''` is not an escaped quote.** Spark reads adjacent literals and
  concatenates them, so EF's default doubling drops the quote instead of
  escaping it; backslash escaping is required.

## 9. Open questions

Resolved:

- ~~Rollback behavior on the stub transaction~~ — moot: transactions are real on
  Thrift, and REST surfaces `NotSupportedException` (no stubs). See §2.1.
- ~~Spec-test subset in CI~~ — start with a curated
  `NorthwindQueryRelationalTestBase`-family subset plus a `DatabricksTestStore`
  seeding Northwind via batched INSERTs; skip migrations suites; run
  `TransactionTestBase` on Thrift only. Manually triggered given warehouse
  latency/cost. See §7.

Still open:

- **Store-generated keys.** The answer so far is "attempt identity-column
  retrieval if the warehouse supports it", but the same note also observes that
  `INSERT … ; SELECT max()` is unsafe, and Databricks has no
  `RETURNING`/`OUTPUT`. The provider currently assumes client-generated keys
  (`ValueGeneratedNever`). Needs a decision in Phase 2: is there a *safe*
  retrieval mechanism worth supporting, or do we validate-and-reject
  store-generated keys with a clear message?
- **`decimal` beyond .NET precision.** The ADO.NET reader surfaces `SqlDecimal`
  above precision 28. EF has no `SqlDecimal` mapping; document the limitation,
  reject it in the model validator, or add a value converter? (Phase 1.)
- **Should the EF integration suite re-run over Thrift**, doubling warehouse
  time, or is a smaller transport-specific suite enough? (Phase 1.)
