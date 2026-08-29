# Databricks.AdoNet.IntegrationTests

End-to-end tests that run against a **live Databricks SQL warehouse**.

## Setup

See [`planning/integration-test-setup.md`](../../planning/integration-test-setup.md) for
step-by-step instructions (workspace URL, warehouse id, personal access token, and the
`DATABRICKS_HOST` / `DATABRICKS_TOKEN` / `DATABRICKS_WAREHOUSE_ID` environment variables).

## Running

```powershell
dotnet test tests/Databricks.AdoNet.IntegrationTests
```

- If the environment variables are **not** set, every test is **skipped** — safe for CI and
  plain `dotnet test` runs at the solution level.
- All DDL happens in a throwaway `adonet_it_<guid>` schema in the `workspace` catalog, which
  is dropped (CASCADE) when the test class completes.
- The first run may take an extra ~30–60 s while a stopped serverless warehouse auto-starts.
