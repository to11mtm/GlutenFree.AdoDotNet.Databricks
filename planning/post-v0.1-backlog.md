# Post-v0.1 Backlog

Items deliberately deferred from the initial release. Additive/non-breaking unless noted.

## v0.2 candidates

### Typed complex-type access
- `GetFieldValue<T>` on `DatabricksDataReader` auto-deserializes JSON for collection/POCO
  types (e.g. `GetFieldValue<List<int>>`, `GetFieldValue<Dictionary<string, int>>`) —
  today users call `JsonSerializer.Deserialize<T>(reader.GetString(i))` themselves.
- Optionally expose the raw Arrow `RecordBatch`/`IArrowArray` for zero-copy consumers
  (e.g. `reader.GetArrowBatch()`), useful for analytics workloads.

### Other candidates
- OAuth U2M (interactive browser) authentication flow.
- Azure AD / Entra ID passthrough authentication.
- `System.Diagnostics.Activity` (OpenTelemetry) spans for statement execution.
- `DbBatch` support (emulated sequentially; the API executes one statement per request).
- Session parameters passthrough in the connection string (e.g. `ANSI_MODE`).
- Result chunk prefetching (download chunk N+1 while N is consumed) for large results.
- linq2db `DatabricksMemberTranslator`: broaden string/math member translations
  (only date-part translations are customized today).
- `DatabricksDecimal`: math helpers as needed (Pow/Round/Truncate), `ISpanFormattable`.

## Longer term
- Thrift/HiveServer2 transport behind `IDatabricksTransport` (all-purpose cluster support,
  real session pooling).
- EF Core provider (separate project).
- Bulk ingest via staging/volume APIs (COPY INTO / streaming ingest) instead of multi-row INSERT.
