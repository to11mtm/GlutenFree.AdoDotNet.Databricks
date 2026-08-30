# GlutenFree.Databricks.AdoNet.IntegrationTests

End-to-end tests that run against a **live Databricks SQL warehouse**.

## Setup

See [`planning/integration-test-setup.md`](../../planning/integration-test-setup.md) for
step-by-step instructions (workspace URL, warehouse id, personal access token, and the
`DATABRICKS_HOST` / `DATABRICKS_TOKEN` / `DATABRICKS_WAREHOUSE_ID` environment variables).

## Running

```powershell
dotnet test tests/GlutenFree.Databricks.AdoNet.IntegrationTests
```

- If the environment variables are **not** set, every test is **skipped** — safe for CI and
  plain `dotnet test` runs at the solution level.
- Tests use **fixed, versioned schemas** (`adodotnet_<name>_v1`) in the `workspace` catalog,
  created with `IF NOT EXISTS` and never dropped. Each run tags its rows with a `run_id`
  GUID and deletes only those rows on cleanup — the metastore table count stays constant
  (dropped managed tables would count against the 500-per-metastore quota for ~7 days due
  to UNDROP retention). If a table's shape must change, bump the version suffix (v1 → v2).
- The first run may take an extra ~30–60 s while a stopped serverless warehouse auto-starts.
