# EF Core Provider Plan

Status: **In progress.** Phase −1, Phase 0 and most of Phase 1 are complete;
**Phase 2 (`SaveChanges`) is the last item before the MVP ships.** See
[§8 Phases and tracking](#8-phases-and-tracking) for the state of each work item.

**MVP scope:** Phases −1 through 2 — a provider that can query *and* save.
Migrations (the old Phase 3) are **post-MVP**, and likely out of scope entirely:
on a lakehouse, schema is normally managed by Databricks (DDL in notebooks/jobs,
Delta Live Tables, Terraform, Unity Catalog governance) rather than by an
application's ORM. The package stays `IsPackable=false` until Phase 2 lands.

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
  `SaveChanges` is incomplete. Phase 2 is the gate for flipping it and cutting
  the first preview.

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
- **REST:** the connection can't begin a transaction, so each
  `ModificationCommandBatch` is made atomic instead:
  `DatabricksAtomicModificationCommandBatch` wraps its statements in
  `BEGIN ATOMIC … END;` and calls `SetRequiresTransaction(false)` so
  `BatchExecutor` does not try to open one. This maps naturally onto EF's batching
  model (a batch already *is* an ordered statement list). An ATOMIC block reports
  no per-statement rows-affected, so nothing that depends on that number is
  supported — concurrency tokens are rejected at model validation, and there are
  no store-generated values to propagate anyway (§2.2).
  Multi-*batch* saves are still non-atomic; document that, and document
  `AutoTransactionBehavior.Never` plus "use the Thrift transport for real
  transactions" as the escape hatches.
- **Choosing between them** happens in
  `DatabricksModificationCommandBatchFactory`, and it has to happen *before* the
  connection is opened (EF completes the first batch before `BatchExecutor` opens
  anything). `DatabricksConnection.SupportsTransactions` therefore answers on a
  closed connection: the Thrift extension declares the capability when it installs
  its transport factory. Wrapping is used only when the transport cannot begin a
  transaction, there is no caller-started transaction, and
  `AutoTransactionBehavior` is not `Never`.
- **No stub transactions.** The earlier InMemory-style
  warn-and-stub design is dropped; where we genuinely cannot provide atomicity
  (REST + explicit `Database.BeginTransaction()`), we surface the ADO.NET
  `NotSupportedException` rather than silently pretending.

Two hard constraints found by running this against a warehouse:

- **`BEGIN ATOMIC` cannot be used over Thrift at all.** The Thrift transport
  emulates named parameters with `EXECUTE IMMEDIATE '<sql>' USING …`, and
  Databricks rejects a SQL script there with `SQL_SCRIPT_IN_EXECUTE_IMMEDIATE`.
  That settles the choice above: Thrift *must* use a real transaction, which is
  the better option anyway.
- **Any transactional write needs `delta.feature.catalogManaged`.** Without it
  the block fails with
  `TRANSACTION_NOT_SUPPORTED.WRITE_NON_CATALOG_MANAGED_TABLE`. That applies to
  the compound statement as much as to `BEGIN TRANSACTION`, so
  `AutoTransactionBehavior.Never` is the documented escape hatch for ordinary
  Delta tables.

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
  (affected-rows check) — post-MVP, because a `BEGIN ATOMIC` block does not
  report per-statement rows affected. For the MVP the validator rejects them.

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
| `IHistoryRepository` | — | **not registered** | migrations are post-MVP (§6); Phase 2 replaces today's DI error with a clear `NotSupportedException` |

**Customized (defaults exist, but the dialect needs them):**

| Service | Status | Why |
|---|---|---|
| `ISqlGenerationHelper` | done | backtick quoting, `:name` parameter markers (match ADO.NET layer & linq2db builder) |
| `IQuerySqlGeneratorFactory` | partial | `LIMIT`/`OFFSET` + `CAST(COUNT AS INT)` done; `APPLY` → `LATERAL` still to do |
| `IMethodCallTranslatorProvider` / `IMemberTranslatorProvider` | partial | string/date/math functions started; see Phase 1 |
| `IAggregateMethodCallTranslatorProvider` | not needed so far | the `COUNT` narrowing is handled at render time in the query SQL generator, which keeps `DISTINCT`/predicate/selector semantics from the shared translator |
| `ISqlExpressionFactory` | not needed yet | only if we need typed expression conveniences |
| `IQueryableMethodTranslatingExpressionVisitorFactory` / `IRelationalSqlTranslatingExpressionVisitorFactory` / `IQueryTranslationPostprocessorFactory` | not needed yet | add as quirks surface (e.g. APPLY→LATERAL rewrites) |
| `IModelValidator` | done (extend in Phase 2) | warns on wide `decimal` columns; Phase 2 adds store-generated-key rejection |
| `IMigrationsSqlGenerator` | post-MVP | schema is managed by Databricks, not EF (§6) |
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

- BIGINT/INT/SMALLINT/TINYINT, DOUBLE/FLOAT, DECIMAL(p,s), STRING (no length
  facets), BOOLEAN, DATE, TIMESTAMP / TIMESTAMP_NTZ (DateTimeOffset/DateTime,
  matching the reader), BINARY, and later ARRAY/MAP/STRUCT (out of scope v1;
  EF 8+ primitive collections could map to ARRAY eventually).
- **Wide decimals map to `DatabricksDecimal`.** Databricks allows
  `DECIMAL(38, s)`, which exceeds .NET `decimal`'s ~28 significant digits.
  `DatabricksDecimal` (BigInteger unscaled value + scale) is mapped as a
  first-class CLR type — *no value converter is involved*, because the ADO.NET
  layer already round-trips it: `DatabricksDataReader.GetFieldValue<DatabricksDecimal>`
  reads it whatever the wire representation, and `DatabricksParameter` binds it
  with an exact `DECIMAL(p, s)` type without narrowing. A converter would be
  strictly worse: it would have to pick a single provider CLR type, while the
  reader's type varies with the column's declared precision.
  The mapping defaults to `DECIMAL(38, 18)` rather than deferring to
  Databricks' own `DECIMAL(10, 0)` default, which would silently truncate.
  - Known limitation: `Queryable.Sum`/`Average` have no overloads for a custom
    struct, so aggregates over a `DatabricksDecimal` property must project to
    `decimal` first. Comparison and ordering translate normally.
- `DatabricksModelValidator` warns when a `decimal` property is mapped to a
  column with precision > 28: such a column only overflows for the rows that
  actually use the extra digits, so it fails in production rather than in
  testing.
- `char` maps to a one-character `STRING` via `CharToStringConverter`.
- Literal generation follows Spark's rules, not the relational defaults —
  see §8's live-warehouse notes for the `||` and backslash-escaping traps.

## 6. Database creation & migrations

**Migrations are post-MVP, and probably not a goal at all.** On a lakehouse the
schema is normally owned by Databricks — DDL in notebooks and jobs, Delta Live
Tables, Terraform, Unity Catalog governance — rather than by an application's
ORM. EF-driven migrations would fight that model, and Delta's DDL surface does
not line up with what EF's migration pipeline expects (constraints are
informational, no `ALTER COLUMN` for arbitrary type changes, DDL cannot run
inside a transaction). Users should manage schema with Databricks' own tooling
and point the provider at existing tables.

- **Done:** `DatabricksDatabaseCreator : RelationalDatabaseCreator`. An EF
  "database" is a Unity Catalog *schema* (the catalog is provisioned out of
  band), so `Create`/`Delete` issue `CREATE SCHEMA` / `DROP SCHEMA … CASCADE`,
  and `Exists`/`HasTables` query the catalog-qualified `information_schema`.
  This is what `EnsureDeleted` and connectivity checks need; it does **not**
  create tables, because that path runs through the migrations SQL generator.
- **Post-MVP, if we do it at all:** `DatabricksMigrationsSqlGenerator` covering
  the Delta-supported DDL subset — CreateTable (Delta types, `USING DELTA`,
  comments, informational PK), DropTable, AddColumn, RenameColumn/Table,
  DropColumn, InsertData/DeleteData/UpdateData — letting the base throw for the
  rest (no SQLite-style table-rebuild machinery). This would also make
  `EnsureCreated` able to create tables.
- **Post-MVP:** `DatabricksHistoryRepository` — a Delta `__EFMigrationsHistory`
  table in the target schema; plain INSERT/DELETE/SELECT, so cheap once the SQL
  generator exists.
- Until then, `Migrate()` fails with a DI resolution error because
  `IHistoryRepository` is unregistered. **Before shipping the MVP** that should
  become a clear `NotSupportedException` explaining that schema is managed
  outside EF — see the Phase 2 checklist.
- Note: DDL cannot run inside an interactive transaction, so any future
  `EnsureCreated`/migration work must not be wrapped in one (§2.1).

## 7. Testing

- **Done — unit tests** (`tests/GlutenFree.EntityFrameworkCore.Databricks.Tests`):
  exact-SQL assertions via `ToQueryString()`, SQL-generation-helper escaping,
  options/extension plumbing, and targeting guards. No warehouse needed.
- **Done — integration tests**
  (`tests/GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests`): follows
  the existing `IntegrationConfig`/`IntegrationFact` pattern; covers
  materialization, parameterized predicates, paging, aggregates and string
  translation against a real warehouse.
- **Done — Phase 1 re-run over Thrift:** the EF suites re-run over the Thrift
  transport via a module initializer plus subclasses
  (`GlutenFree.EntityFrameworkCore.Databricks.Thrift.IntegrationTests`), matching
  what the ADO.NET and linq2db suites do. Phase 2's save tests should be written
  in the same shared-base style so they are re-run for free.
- **Post-MVP — EF spec tests:** package
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

**MVP = Phases −1 → 2.** Phase 2 is the gate for flipping `IsPackable` and
cutting the first preview package. Everything below
[Post-MVP](#post-mvp) is explicitly out of that first release.

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

### Phase 1 — query provider — **COMPLETE**

The read-only story, which is already the bulk of the value for a lakehouse.

Done so far:

- [x] `LIMIT ALL` for an unbounded `Skip` (a `BIGINT` bound is rejected)
- [x] `CAST(COUNT(...) AS INT)` / `CAST(SUM(...) AS INT|FLOAT)` narrowing, done at
      render time so `DISTINCT`, predicates and selectors keep the shared
      translator's semantics
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
- [x] **Arbitrary-precision decimals.** `DatabricksDecimal` is mapped as a
      first-class CLR type (no value converter needed — see §5), so
      `DECIMAL(29..38, s)` columns round-trip losslessly. `DatabricksModelValidator`
      warns when a `decimal` is pointed at a column wider than it can hold.
      Verified live: 38-digit values materialize, compare, order and bind as
      parameters server-side.

Remaining **for the MVP**: none.

- [x] **`GroupBy` beyond simple aggregates** — composite keys, computed keys,
      `HAVING` (on `COUNT` and on `SUM`), nullable keys (all `NULL`s land in one
      group), ordering by an aggregate, filters before *and* after grouping, and
      `Distinct().Count()` inside a group, all verified live
      (`EfCoreGroupingIntegrationTests`).
- [x] **Nullability/`??` semantics** — `IS NULL`/`IS NOT NULL`, null-valued
      parameters, EF's widened inequality, `COALESCE` in projections and
      predicates, `IsNullOrEmpty`, `HasValue`, aggregates over nullable columns
      and NULL sort order, all verified live
      (`EfCoreNullSemanticsIntegrationTests`).

Deferred to [post-MVP](#post-mvp): the Northwind spec-test subset and the
`Int128` mapping for zero-scale decimals.

**Exit criteria (MVP):** the shipped query surface is exercised by the
provider's own integration suites over both transports, and no common LINQ shape
silently falls back to client evaluation in a `WHERE` clause. (The Northwind
spec suite raises that bar and is post-MVP.)

### Phase 2 — `SaveChanges` — **COMPLETE**

This was the gate for shipping: a provider that queries but cannot save is not
something to hand out for feedback.

- [x] `BEGIN ATOMIC … END;`-wrapping `DatabricksAtomicModificationCommandBatch`
      for the REST transport, with rows-affected verification dropped for wrapped
      batches (§2.1)
- [x] One statement per batch on Thrift, inside a real transaction opened by
      `BatchExecutor` — both because that is closer to what EF expects and
      because `EXECUTE IMMEDIATE` rejects a compound statement (§2.1)
- [x] `DatabricksValueGenerationConvention`: every property defaults to
      `ValueGenerated.Never` (§2.2), so the common case needs no configuration
- [x] `UPDATE`/`DELETE` no longer emit the relational base's `RETURNING 1`,
      which Databricks rejects outright
- [x] Extend `DatabricksModelValidator`: reject store-generated properties and
      concurrency tokens with messages that name the alternative
      (it already warns about wide `decimal` columns)
- [x] Replace the `Migrate()` DI failure with a clear `NotSupportedException`
      pointing at Databricks-managed schema (§6), via a `DatabricksMigrator`
      registered for `IMigrator`
- [x] Live integration tests: insert/update/delete, multi-entity saves, atomic
      rollback of a failing batch, the `AutoTransactionBehavior.Never` escape
      hatch, and commit/rollback of an explicit transaction over Thrift
- [x] README: document the per-transport atomicity guarantees, the
      `catalogManaged` requirement and the client-generated-key requirement
- [x] `IsPackable` flipped. The EF provider is versioned independently (major
      tracks the EF Core major), so it is released by its own `efcore-v*` tag and
      excluded from the solution-wide `v*` pack — see `.github/workflows/release.yml`.

**Exit criteria (met):** CRUD round-trips against a live warehouse on both
transports, atomicity guarantees documented per transport, and `IsPackable`
flipped to ship the preview.

### Post-MVP

Deliberately deferred so a work-in-progress preview can ship for feedback.

- [ ] **Curated Northwind spec-test subset** —
      `NorthwindQueryRelationalTestBase`-family with a `DatabricksTestStore`
      seeding Northwind via batched INSERTs (§7). This is the large one: it
      raises confidence in query coverage well beyond our own suites, but it is
      slow, warehouse-hungry, and not needed to gather feedback on the shape of
      the provider.
- [ ] **`Int128` for zero-scale decimals.** `Int128.MaxValue` (~1.70×10³⁸)
      exceeds `DECIMAL(38, 0)`'s maximum (10³⁸−1), so *every* zero-scale
      Databricks decimal fits losslessly — it is a natural integral mapping for
      columns that are conceptually counters or identifiers, and gives real
      integer arithmetic instead of `DatabricksDecimal`'s decimal semantics.
      Implementation sketch: a `DatabricksInt128TypeMapping` with store type
      `DECIMAL(38, 0)` and a `ValueConverter<Int128, DatabricksDecimal>`, so it
      rides the already-working `DatabricksDecimal` read/write path and inherits
      its "precision must not exceed 38" guard — no ADO.NET changes needed.
      Opt-in via the property's CLR type, as EF expects.
- [ ] **Migrations** (the old Phase 3) — `DatabricksMigrationsSqlGenerator` and
      `DatabricksHistoryRepository`, which would also give `EnsureCreated` the
      ability to create tables. **Likely not a goal**: lakehouse schema is
      normally managed by Databricks rather than an application ORM, and Delta's
      DDL surface does not match EF's migration model (§6). Revisit only if
      users ask for it.
- [ ] Scaffolding (`IDatabaseModelFactory` / `IProviderCodeGenerator` in a
      Design sub-package) — reverse-engineering an *existing* Databricks schema
      is a much better fit for this ecosystem than forward-engineering one, so
      this is the more valuable half of the tooling story.
- [ ] `ARRAY`/`MAP`/`STRUCT` and primitive collections → `ARRAY`
- [ ] MERGE-based upsert optimizations
- [ ] **Optimistic-concurrency tokens** via `UPDATE … WHERE token = old` plus an
      affected-rows check. Out of the MVP: it needs per-statement rows-affected,
      which a `BEGIN ATOMIC` block does not report, so it would only ever work on
      one transport. Until then the validator rejects concurrency tokens rather
      than silently ignoring them (§2.2).
- [ ] Store-generated identity keys
- [ ] Retrying execution strategy tuned to warehouse cold starts

### Behaviors found only by running against a live warehouse

Worth keeping in mind when extending the provider — none of these surface in
offline SQL-generation tests:

- **`LIMIT` must be an `INT` expression.** `OFFSET` requires a `LIMIT`, and a
  `BIGINT` bound is rejected with `INVALID_LIMIT_LIKE_EXPRESSION.DATA_TYPE`, so
  an unbounded skip emits `LIMIT ALL`.
- **`COUNT` returns `BIGINT`, and so does `SUM` over any integral column**
  (`SUM` over a `FLOAT` returns `DOUBLE`). EF materializes `Count`/`Sum` after
  the CLR selector and the reader will not narrow, so the generator wraps those
  aggregates in `CAST(... AS INT)`/`CAST(... AS FLOAT)`.
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
- **`NULL`s sort first ascending, last descending.** EF emits no explicit
  `NULLS FIRST`/`NULLS LAST` clause, so this is what applications see. It matches
  SQL Server ascending but is the inverse of PostgreSQL.
- **`RETURNING` does not exist.** The relational `UpdateSqlGenerator` appends
  `RETURNING 1` to every `UPDATE`/`DELETE` to learn the rows affected; Databricks
  fails that with `PARSE_SYNTAX_ERROR`, so both operations are overridden to emit
  plain DML.
- **`EXECUTE IMMEDIATE` rejects SQL scripts.** The Thrift transport binds named
  parameters through it, so a `BEGIN ATOMIC … END;` block can never be used over
  Thrift (`SQL_SCRIPT_IN_EXECUTE_IMMEDIATE`).
- **Transactional writes need `delta.feature.catalogManaged`.** Both transaction
  modes fail on an ordinary Delta table with
  `TRANSACTION_NOT_SUPPORTED.WRITE_NON_CATALOG_MANAGED_TABLE`.
- **Interactive transactions conflict at table scope.** A save inside one fails
  with `DELTA_CONCURRENT_DELETE_READ` if anything else deletes from the table
  concurrently — which is why the integration assemblies run their suites
  serially.

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
