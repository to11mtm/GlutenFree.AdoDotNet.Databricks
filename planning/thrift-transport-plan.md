# Thrift Transport Plan (future work — targeted for v0.3+)

Status: **Speculative / not started.** This document specs out what adding a
Thrift/HiveServer2 transport behind `IDatabricksTransport` would involve, so the
work can be picked up later without re-deriving the research.

## 1. Why a Thrift transport?

The current transport (`RestStatementTransport`) uses the public
[Statement Execution API](https://docs.databricks.com/api/workspace/statementexecution)
(`/api/2.0/sql/statements`). It is simple, documented, and stable — but it has
real limitations that a Thrift transport would lift:

| Concern | REST Statement Execution API | Thrift (HiveServer2/TCLIService) |
|---|---|---|
| Compute targets | SQL warehouses only | SQL warehouses **and** all-purpose/interactive clusters |
| Sessions | None (each statement is standalone) | Real sessions: `USE catalog/schema`, session confs, temp views persist |
| Latency | Submit + hybrid wait + polling; extra round trips per statement | Long-lived session; `ExecuteStatement` with DirectResults can return rows in one round trip |
| Result fetch | Chunked, external presigned links (Arrow) or inline JSON | `FetchResults` streaming, plus CloudFetch (presigned links) for large results |
| Row/size limits | 100 GiB / result, 25 MiB inline | Effectively the same backend limits, but no inline-JSON 25 MiB constraint |
| Protocol stability | Public, versioned, documented | De-facto stable (all official Databricks drivers use it) but **not a documented public API** |

Every official Databricks driver (JDBC, ODBC, `databricks-sql-python`,
`databricks-sql-go`, `databricks-sql-nodejs`) speaks Thrift, so the wire
behavior is well understood and battle-tested — those drivers are our primary
references for correct behavior.

## 2. Protocol overview

- **IDL:** Hive's `TCLIService.thrift` plus Databricks/Spark extensions
  (`TSparkGetDirectResults`, `TSparkArrowResultLink`, `TDBSqlSessionConf`, etc.).
  The authoritative copy to vendor is the one shipped in
  [`databricks/databricks-sql-go`](https://github.com/databricks/databricks-sql-go)
  (`internal/cli_service.thrift`) or `databricks-sql-python`
  (`src/databricks/sql/thrift_api/TCLIService/TCLIService.thrift`).
- **Protocol version:** negotiate `SPARK_CLI_SERVICE_PROTOCOL_V6` (or newest
  the peer supports) in `OpenSession`. V5+ is required for Arrow + CloudFetch.
- **Transport:** Thrift-over-HTTP (`THttpTransport`), **not** raw sockets:
  - SQL warehouse endpoint: `https://<host>/sql/1.0/warehouses/<warehouse-id>`
  - All-purpose cluster endpoint: `https://<host>/sql/protocolv1/o/<org-id>/<cluster-id>`
  - Auth is the same `Authorization: Bearer <token>` header we already produce
    via `IDatabricksAuthenticator` (PAT and OAuth M2M both work unchanged).
  - Binary Thrift protocol (`TBinaryProtocol`) over the HTTP body.
- **Core RPC flow:**
  1. `OpenSession` → `TSessionHandle` (holds `sessionId` GUID bytes)
  2. `ExecuteStatement(sessionHandle, sql, confOverlay, getDirectResults, ...)`
     → `TOperationHandle`
  3. If DirectResults didn't complete inline: poll `GetOperationStatus`
  4. `GetResultSetMetadata` → schema (+ `arrowSchema` bytes when Arrow enabled)
  5. `FetchResults` loop → `TRowSet` containing either:
     - `arrowBatches` (inline Arrow IPC record batches, optionally LZ4-framed), or
     - `resultLinks` (`TSparkArrowResultLink[]` — CloudFetch presigned URLs), or
     - legacy `columns` (columnar Thrift values — we should *not* need this path)
  6. `CloseOperation`, and eventually `CloseSession`
- **Cancellation:** `CancelOperation(operationHandle)` — best effort, same
  semantics as our current REST cancel.
- **Parameters:** protocol V8+ supports native named parameters
  (`TSparkParameter`); earlier versions require client-side literal inlining.
  Our REST path already sends typed named parameters, so require a protocol
  version with parameter support and fail fast otherwise.

## 3. Fit against `IDatabricksTransport`

The interface was designed with this in mind, but there are seams to resolve:

| Interface member | REST semantics today | Thrift mapping | Gap / decision needed |
|---|---|---|---|
| `ExecuteStatementAsync` | POST + hybrid wait + poll → `StatementResponse` w/ string `statementId` | `OpenSession` (lazy, once) + `ExecuteStatement` + DirectResults/poll | `statementId` is a string; Thrift handles are structs (guid+secret bytes). Encode handle as base64 in the existing string field, or widen `StatementResponse` with an opaque handle. **Preferred: opaque handle** — keep `StatementId` as a transport-owned token. |
| `GetResultChunkAsync(id, chunkIndex)` | GET chunk N metadata/links | `FetchResults` is a forward cursor, not random access by index | Transport keeps per-statement fetch state; treat `chunkIndex` as "next expected" and throw on out-of-order access (the reader only ever walks forward, so this is safe today — add a test locking that in). |
| `DownloadExternalLinkAsync` | GET presigned URL | Identical for CloudFetch links (`TSparkArrowResultLink.fileLink`) | None — reuse the existing shared `HttpClient` download path, including the "no bearer token on presigned URLs" rule. |
| `CancelStatementAsync` | POST cancel | `CancelOperation` | None. |
| Sync counterparts | Genuinely sync via `HttpClient.Send` | Thrift-over-HTTP lets us reuse `HttpClient.Send` if we own the HTTP layer (see §4 codegen decision) | The stock ApacheThrift `THttpTransport` is async-only under the hood; a hand-rolled HTTP layer keeps our "genuinely synchronous end-to-end" guarantee. |
| `IAsyncDisposable` | Disposes owned handler only | Must also `CloseSession` best-effort | Add session teardown; never dispose the shared `HttpClient`. |

Additional connection-level items:

- **Session lifetime:** one `TSessionHandle` per open `DatabricksConnection`.
  `Open` → `OpenSession`; `Close` → `CloseSession`. This finally gives
  `ChangeDatabase`/`ChangeCatalog` true session semantics (today we replay
  `USE` statements per command).
- **Session expiry/idle timeout:** handle `INVALID_HANDLE`/session-expired
  errors by surfacing a clear `DatabricksException` (broken-connection state),
  matching ADO.NET expectations. Do not auto-reopen silently.
- **Heartbeats:** not required; warehouses keep sessions alive while operations
  run. Document the idle timeout (~15 min typical) in the README.

## 4. Thrift codegen strategy

Options, in order of preference:

1. **Vendor generated C# + hand-rolled HTTP layer (preferred).**
   Run the Apache Thrift compiler once against the vendored
   `TCLIService.thrift`, commit the generated code under
   `src/GlutenFree.Databricks.AdoNet/Transport/Thrift/Generated/` (with a
   regeneration script + pinned compiler version), and depend on the
   `ApacheThrift` runtime NuGet only for protocol serialization
   (`TBinaryProtocol`, `TMemoryBufferTransport`). We then do HTTP ourselves:
   serialize the request struct to a byte buffer, POST it with our existing
   retry/auth/HTTPS-only machinery (sync **and** async), deserialize the
   response buffer. This preserves the sync pipeline and keeps our
   429/503/Retry-After handling in one place.
2. **ApacheThrift runtime end-to-end** (`THttpTransport`): least code, but
   async-only, owns its own HttpClient, and its retry story conflicts with ours.
3. **Hand-written minimal codec** (no ApacheThrift dependency): TCLIService's
   surface we need is ~10 RPCs, but the structs are large and versioned;
   maintenance cost likely exceeds the value. Keep as fallback only if the
   ApacheThrift runtime proves problematic (e.g., trimming/AOT issues).

Generated-code hygiene: mark the generated namespace `internal`, exclude from
coverage, suppress style analyzers for that folder via `.editorconfig`.

## 5. Result decoding

Good news: the hard part is already done. `ArrowResultDecoder` /
`DatabricksTypeMap.ConvertArrowValue` consume Arrow IPC record batches and are
transport-agnostic. Thrift work is only about *acquiring* the bytes:

- `arrowBatches`: each `TSparkArrowBatch.batch` is an IPC record batch **without
  the schema preamble** — prepend `GetResultSetMetadata().arrowSchema` to form a
  valid IPC stream before handing to `ArrowStreamReader`. Handle optional
  LZ4-frame compression (`lz4Compressed` flag) — we already reference
  `Apache.Arrow.Compression`.
- `resultLinks` (CloudFetch): identical to today's EXTERNAL_LINKS path;
  links expire (~15 min), so keep the existing just-in-time download model.
  Set `canDownloadResult=true` in `ExecuteStatement` to opt in.
- Set `canReadArrowResult=true` and request `TSparkArrowTypes` with
  `timestampAsArrow`, `decimalAsArrow`, `complexTypesAsArrow`,
  `intervalTypesAsArrow` = true so decoding matches the REST Arrow shapes we
  already test (complex types as genuine nested arrays, intervals as Arrow
  interval arrays).
- **Watch item:** verify TIMESTAMP_NTZ and interval representations match
  the REST-Arrow behavior our reader expects; add integration tests that run
  the full extended-types matrix through the Thrift transport.

## 6. Configuration surface

Connection-string additions (builder + docs):

- `Transport=Rest|Thrift` (default `Rest`) — selects the transport factory.
- `HttpPath=/sql/1.0/warehouses/<id>` — optional explicit path; defaults to the
  warehouse path derived from `WarehouseId`; required form for all-purpose
  clusters. (JDBC/ODBC users will recognize `HTTPPath`.)
- Existing settings reused as-is: `Host`, `Token`/OAuth settings,
  `CommandTimeout`, `ConnectionTimeout`, catalog/schema.

`DatabricksConnection` gains a small transport-factory switch (the
`TransportFactory` test hook already exists). Shared `HttpClient` from
`DatabricksSharedResources` is reused for both Thrift POSTs and CloudFetch
downloads.

## 7. Testing plan

- **Unit:** fake Thrift server = in-memory handler that speaks
  binary-protocol-over-HTTP (serialize expected responses with the same
  generated code); cover OpenSession negotiation, DirectResults short-circuit,
  poll loop, arrowBatches schema-prepend, LZ4 batches, CloudFetch links,
  forward-only chunk guard, cancel, session close on dispose, sync parity.
- **Integration:** run the *entire existing* integration suite (all 5 fixtures)
  a second time with `Transport=Thrift` via a test-collection parameter or env
  var (`DATABRICKS_TRANSPORT=thrift`) — the fixed versioned-schema/run_id
  pattern needs no changes. Add a session-semantics test (`USE schema` sticks
  across commands) that only runs on Thrift.
- **Dialect/linq2db:** no changes expected — SQL generation is
  transport-independent — but run the linq2db integration fixtures on Thrift
  once as a smoke pass.

## 8. Risks & open questions

1. **Unofficial protocol:** TCLIService + Spark extensions are not a documented
   public API. Mitigation: pin behavior to what `databricks-sql-go`/`-python`
   do; keep REST as the default transport.
2. **Thrift IDL drift:** vendor the IDL with a source URL + commit hash;
   regeneration is deliberate, not automatic.
3. **ApacheThrift NuGet health:** verify current package versions, net8.0
   support, and trimming friendliness before committing to option 1; fallback
   is option 3 (hand-written codec).
4. **Parameter support:** confirm minimum protocol version for
   `TSparkParameter` named parameters on both warehouses and all-purpose
   clusters; decide whether pre-V8 peers get an error or literal inlining
   (error preferred — inlining reintroduces injection risk).
5. **GetSchema:** Thrift offers metadata RPCs (`GetCatalogs`, `GetSchemas`,
   `GetTables`, `GetColumns`) — optional optimization; the existing
   `information_schema` queries work unchanged, so defer.
6. **Cloud Fetch link expiry under slow consumers:** links live ~15 min;
   confirm re-fetch behavior (`FetchResults` with same start row?) or document
   the limitation, mirroring what the Go driver does.

## 9. Suggested milestones

1. **Spike:** vendor IDL, generate code, hand-rolled HTTP POST of
   `OpenSession`/`ExecuteStatement`/`FetchResults` against the live warehouse;
   prove arrowBatches decode through the existing reader. (No public surface.)
2. **Transport:** `ThriftStatementTransport : IDatabricksTransport` with
   session management, DirectResults, polling, cancel, sync parity, retries.
3. **Wiring:** connection-string `Transport`/`HttpPath`, factory switch, docs.
4. **Test matrix:** fake-server unit tests + dual-transport integration runs.
5. **Stretch:** all-purpose cluster support, Thrift metadata RPCs for
   `GetSchema`, session-conf passthrough.
