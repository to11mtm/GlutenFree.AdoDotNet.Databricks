# Databricks ADO.NET Provider — Plan

## Decisions (confirmed with user)
- Protocol: REST Statement Execution API first, transport abstraction (`IDatabricksTransport`) so Thrift can be added later
- TFM: net8.0+ only, modern APIs
- Results: Apache.Arrow dependency, ARROW_STREAM format (JSON_ARRAY fallback)
- Naming: `Databricks.AdoNet` namespace/package, `Databricks*` class prefix

## Phases
1. Requirements doc (done — planning/Adodotnet-databricks-provider.md)
2. Project scaffolding (src/tests/slnx, editorconfig, CI later)
3. Connection string builder + parsing
4. Auth (PAT, OAuth M2M client credentials, U2M later)
5. Transport abstraction + REST Statement Execution client
6. Core ADO.NET surface: DbConnection, DbCommand, DbParameter(Collection), DbDataReader
7. Arrow result decoding + type mapping
8. DbProviderFactory, batch, transactions (limited — see reqs), pooling story
9. Tests: unit w/ mocked HTTP; integration gated on env vars
10. Docs, samples, NuGet packaging

## Work tracking: todos table in session SQL DB
