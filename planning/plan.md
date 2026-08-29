# Databricks ADO.NET Provider — Plan

## Decisions (confirmed with user)
- Protocol: REST Statement Execution API first, transport abstraction (`IDatabricksTransport`) so Thrift can be added later
- TFM: net8.0+ only, modern APIs
- Results: Apache.Arrow dependency, ARROW_STREAM format (JSON_ARRAY fallback)
- Naming: `Databricks.AdoNet` namespace/package, `Databricks*` class prefix

## Phases
1. ✅ Requirements doc (planning/Adodotnet-databricks-provider.md)
2. ✅ Project scaffolding (src/tests/slnx, Directory.Build.props, editorconfig)
3. ✅ Connection string builder + parsing (validation, secret redaction)
4. ✅ Auth (PAT, OAuth M2M w/ cached single-flight refresh; U2M later)
5. ✅ Transport abstraction + REST Statement Execution client (retry, polling, cancel)
6. ✅ Core ADO.NET surface: DbConnection, DbCommand, DbParameter(Collection), DbDataReader
7. ✅ Arrow result decoding + type mapping (SqlDecimal for p>28, LZ4 via Apache.Arrow.Compression)
8. ✅ DbProviderFactory + GetSchema (Catalogs/Schemas/Tables/Views/Columns via system.information_schema, parameterized restrictions)
9. ✅ Unit tests incl. Dapper smoke tests (113 passing)
10. ✅ Integration tests: separate tests/Databricks.AdoNet.IntegrationTests project, env-var
    gated (9 passing against live Free Edition warehouse; setup: planning/integration-test-setup.md)
11. ✅ linq2db provider: src/Databricks.AdoNet.Linq2Db (DataProvider, SqlBuilder w/ backticks +
    :param markers + LIMIT/OFFSET + Databricks DDL type names, SqlOptimizer w/ alt DELETE/UPDATE,
    LockedMappingSchema literals, SchemaProvider over information_schema, MemberTranslator defaults;
    DatabricksTools/UseDatabricks entry points; linq2db pinned [6.4.0,7.0.0); tests split into
    tests/Databricks.AdoNet.Linq2Db.Tests (8 smoke) + tests/Databricks.AdoNet.Linq2Db.IntegrationTests (5 live))
12. ✅ CI + packaging: .github/workflows/{ci,release,integration}.yml; src/Directory.Build.props
    adds SourceLink (GitHub), snupkg symbols, packed README, VersionPrefix 0.1.0 (release version
    derived from v* tag); RepositoryUrl inferred from git remote (org-move safe); secrets needed:
    NUGET_API_KEY (release), DATABRICKS_* (manual integration workflow)
13. ✅ Docs: root README.md (quickstart ADO.NET/Dapper/linq2db, connection-string reference,
    type-mapping table, limitations); LICENSE = Apache-2.0 (canonical text at repo root,
    PackageLicenseExpression in Directory.Build.props)
14. Stretch: DatabricksDecimal BigDecimal-style struct

## Notes
- Projects renamed to GlutenFree.* prefix (user, 2026-08-29).
- planning/token-info.md contains a live PAT: gitignored, verified never committed
  (searched all blobs for token value + id — clean).
- linq2db live-testing dialect fixes: SupportsColumnAliasesInSource=false and
  IsValuesSyntaxSupported=false (Databricks MERGE constraints); APPLY→LATERAL via BuildJoinType;
  DatabricksBulkCopy routes MultipleRows/ProviderSpecific/Default to MultipleRowsCopy1;
  string literals escape quotes with backslash (Spark treats '' as literal concatenation).
- Sync API is genuinely synchronous (HttpClient.Send pipeline end to end); async remains the
  recommended default (documented in README "Async vs. sync").
- Live linq2db test coverage: 21 integration tests (dialect + SQL bits + mapping schema).

## Live-test findings (corrected assumptions)
- ARROW_STREAM delivers ARRAY/MAP/STRUCT as real Arrow nested arrays (not JSON strings);
  provider serializes them to JSON strings per v1 type mapping.
- DML results include num_affected_rows among multiple columns; located by name.

## Work tracking: todos table in session SQL DB
