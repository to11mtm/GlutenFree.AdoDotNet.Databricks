# Databricks.AdoNet.Linq2Db.IntegrationTests

End-to-end linq2db tests against a **live Databricks SQL warehouse**.

Setup and gating are identical to the base integration tests — see
[`planning/integration-test-setup.md`](../../planning/integration-test-setup.md).
Tests are skipped automatically unless `DATABRICKS_HOST`, `DATABRICKS_TOKEN`, and
`DATABRICKS_WAREHOUSE_ID` are set.

```powershell
dotnet test tests/Databricks.AdoNet.Linq2Db.IntegrationTests
```
