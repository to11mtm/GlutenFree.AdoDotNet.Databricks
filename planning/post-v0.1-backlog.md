# Post-v0.1 Backlog

Items deliberately deferred from the initial release. Additive/non-breaking unless noted.

## v0.2 candidates

### Typed complex-type access (keep ADO.NET and linq2db at parity)

**ADO.NET (baseline):**
- `GetFieldValue<T>` on `DatabricksDataReader` auto-deserializes JSON for collection/POCO
  types (e.g. `GetFieldValue<List<int>>`, `GetFieldValue<Dictionary<string, int>>`) —
  today users call `JsonSerializer.Deserialize<T>(reader.GetString(i))` themselves.
- Optionally expose the raw Arrow `RecordBatch`/`IArrowArray` for zero-copy consumers
  (e.g. `reader.GetArrowBatch()`), useful for analytics workloads.

**linq2db (`ARRAY`/`MAP`/`STRUCT`/`VARIANT`) — should ship alongside the baseline items so
the provider keeps feature parity:**
- Mapping-schema conversions so entity properties can be typed collections/POCOs instead
  of raw JSON strings — `SetConvertExpression<string, T>` (JSON-deserialize on read,
  building on the baseline `GetFieldValue<T>` work) plus literal/parameter rendering on
  write (`array(...)`, `map(...)`, `named_struct(...)` constructors; note the Statement
  Execution API cannot bind complex-typed parameters, so writes must render as SQL
  literals or go through `to_json`/`from_json`).
- Server-side member translations for element access, mirroring what the SQL bits tests do
  with `Sql.Expr` today: `list[i]` → `arr[i]`, dictionary indexer → `mp['k']`,
  struct member → `st.field`; consider `Sql.Ext` helpers for `explode`/`array_contains`/
  `size`/`element_at` and higher-order functions (`transform`, `filter`).
- `VARIANT` path functions (`variant_get`, `:` path syntax) behind typed helpers.
- Bulk copy / insert with complex-typed columns currently unsupported (JSON-string columns
  work); decide whether to render constructor literals in `MultipleRowsCopy1` value lists.
- Schema provider: surface `ARRAY<...>`/`MAP<...>`/`STRUCT<...>` `full_data_type` text as
  richer scaffolding metadata (today they scaffold as `string`).

### Other candidates
- Pooled memory buffers for Arrow deserialization (e.g. `ArrayPool<byte>` or a pattern similar to Cysharp's array Pools).
- OAuth U2M (interactive browser) authentication flow.
- Azure AD / Entra ID passthrough authentication.
- `System.Diagnostics.Activity` (OpenTelemetry) spans for statement execution.
- `DbBatch` support (emulated sequentially; the API executes one statement per request).
- Session parameters passthrough in the connection string (e.g. `ANSI_MODE`).
- Result chunk prefetching (download chunk N+1 while N is consumed) for large results.
- linq2db `DatabricksMemberTranslator`: broaden string/math member translations
  (only date-part translations are customized today).
- `DatabricksDecimal`: math helpers as needed (Pow/Round/Truncate), `ISpanFormattable`.
- `DatabricksDecimal` perf: internal `Int128` fast path (any Databricks DECIMAL(38) unscaled
  value fits in Int128; ~1.7e38 max) with BigInteger widening fallback for intermediate
  arithmetic overflow (multiplication/scale-alignment can need up to ~76 digits); also read
  Arrow `Decimal128` 16-byte two's-complement buffers directly into Int128 (zero-parse) in
  `DatabricksTypeMap`. Public semantics stay arbitrary-precision — Int128 is internal only.

## Longer term
- Thrift/HiveServer2 transport behind `IDatabricksTransport` (all-purpose cluster support,
  real session pooling). Detailed spec: [thrift-transport-plan.md](thrift-transport-plan.md).
- EF Core provider (separate project).
- Bulk ingest via staging/volume APIs (COPY INTO / streaming ingest) instead of multi-row INSERT.
