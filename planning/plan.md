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
9. Unit test gap-filling (Dapper smoke test) — 65 tests passing so far
10. Integration tests gated on DATABRICKS_HOST/TOKEN/WAREHOUSE_ID env vars
11. CI (GitHub Actions), NuGet packaging (SourceLink, license choice)
12. Docs (README quickstart, type-mapping table, limitations), samples
13. Stretch: DatabricksDecimal BigDecimal-style struct

## Work tracking: todos table in session SQL DB
