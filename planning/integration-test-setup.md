# Integration Test Setup

The integration tests are **skipped automatically** unless the environment variables below are
set, so nothing here blocks unit-test or CI workflows. To run them against your Databricks
Free Edition (community) account, follow these steps.

## 1. Find your workspace URL

1. Log in at https://login.databricks.com (Free Edition).
2. Copy the browser URL host once you're in the workspace — it looks like
   `https://dbc-a1b2c3d4-e5f6.cloud.databricks.com`.
   That full `https://...` value is your **Host**.

## 2. Find (or create) a SQL warehouse

Free Edition ships with a serverless SQL warehouse.

1. In the left sidebar choose **SQL Warehouses**.
2. Click the existing warehouse (e.g. *Serverless Starter Warehouse*) — or create one.
3. On the **Connection details** tab, copy either:
   - **HTTP path** (looks like `/sql/1.0/warehouses/abcdef1234567890`), or
   - just the trailing warehouse id (`abcdef1234567890`).

## 3. Create a personal access token (PAT)

1. Click your avatar (top right) → **Settings** → **Developer**.
2. Next to **Access tokens**, click **Manage** → **Generate new token**.
3. Give it a comment like `adonet-integration-tests`, set a lifetime, and copy the token
   (starts with `dapi...`). You cannot view it again later.

> If the Access tokens option is unavailable on your account tier, let me know — we can fall
> back to an OAuth M2M service principal (the provider already supports `AuthType=OAuthM2M`),
> though service principal creation may also be restricted on Free Edition.

## 4. Set the environment variables

PowerShell (current session only):

```powershell
$env:DATABRICKS_HOST = "https://dbc-a1b2c3d4-e5f6.cloud.databricks.com"
$env:DATABRICKS_TOKEN = "dapi..."
$env:DATABRICKS_WAREHOUSE_ID = "abcdef1234567890"
```

Or persist them for your user account (new terminals only):

```powershell
[Environment]::SetEnvironmentVariable("DATABRICKS_HOST", "https://dbc-....cloud.databricks.com", "User")
[Environment]::SetEnvironmentVariable("DATABRICKS_TOKEN", "dapi...", "User")
[Environment]::SetEnvironmentVariable("DATABRICKS_WAREHOUSE_ID", "abcdef1234567890", "User")
```

> **Never commit the token** to the repository or paste it into files/chat. If it leaks,
> revoke it from the same Settings page and generate a new one.

## 5. Run the integration tests

```powershell
dotnet test --filter "Category=Integration"
```

When the variables are absent the same command reports the tests as skipped.

## Notes / expectations

- Tests use fixed, versioned schemas (`adodotnet_<name>_v1`) in the `workspace` catalog;
  each run's rows are tagged with a `run_id` GUID and deleted afterwards. Tables are created
  once with `IF NOT EXISTS` and never dropped (dropped managed tables would count against
  the metastore table quota for ~7 days); nothing else in the workspace is touched.
- Free Edition serverless warehouses auto-stop; the first test run may take ~30–60 s extra
  while the warehouse starts.
- Free Edition has rate limits; if you see 429 retries in test output, that's expected and
  handled by the provider's backoff.
