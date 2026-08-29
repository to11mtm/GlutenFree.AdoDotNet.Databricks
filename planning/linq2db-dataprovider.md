### Reference Material:

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteDataProvider.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteSqlBuilder.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteSqlOptimizer.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteMappingSchema.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteBulkCopy.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteSchemaProvider.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteSqlExpressionConvertVisitor.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/Translation/SQLiteMemberTranslator.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteSpecificTable.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/SqlProvider/SqlProviderFlags.cs

May not be needed but for reference:

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteProviderAdapter.cs

https://github.com/linq2db/linq2db/blob/master/Source/LinqToDB/Internal/DataProvider/SQLite/SQLiteProviderDetector.cs

https://github.com/linq2db/linq2db/tree/master/Source/LinqToDB/DataProvider/SQLite

### Stuff Worth Noting:

Databricks Does not support `CROSS APPLY`/`OUTER APPLY`, But does have `INNER JOIN LATERAL`/`LEFT JOIN LATERAL` as Equivalents.

We should consider the other SqlProviderFlags carefully, and ensure that we are claiming the correct ones for Databricks. For example, we should ensure that we are claiming `SupportsCommonTableExpressions` and `SupportsWindowFunctions` since Databricks does support these features.

### Stuff Worth Testing:

 - `DataProvider` (DatabricksDataProvider) - implement `GetSchemaProvider()`, `GetSqlBuilder()`, `GetSqlOptimizer()`, `GetMappingSchema()`, `BulkCopy()` and any other required methods. This is the main entry point for linq2db to interact with the provider.
 - Mapping Schema.
 - SQL Bits:
   - `.SelectQuery()` - Linq2Db inline selects, e.x. `db.SelectQuery(() => new { foo="a", bar="1" })` should produce `SELECT 'a' AS foo, '1' AS bar`.
   - Window Functions
   - Joins including `LEFT JOIN`, `RIGHT JOIN`, `INNER JOIN`, `FULL OUTER JOIN`, `CROSS JOIN`, `NATURAL JOIN`, `APPLY/CROSS APPLY AKA LATERAL JOIN`
   - Group By
   - Order By
   - Limit / Offset
   - Subqueries
   - CTEs (Common Table Expressions)
   - `IN` / `NOT IN`
   - Insert
   - Update
   - Delete
   - Parameterized queries
   - Bulk Copy Functionality (via BulkCopyAsync, MultipleRows, etc.)
     - Keep the number of rows small (i.e. 5 or less) we just want to verify that the bulk copy functionality is working, not performance.
   - MERGE support
 - Ensure chosen SqlProviderFlags are accurate and optimal.

### Status (handled 2026-08-29)

**Stuff Worth Noting — done:**
- APPLY→LATERAL: `DatabricksSqlBuilder.BuildJoinType` emits `INNER JOIN LATERAL` / `LEFT JOIN LATERAL`
  (PostgreSQL pattern) and flags `IsApplyJoinSupported` + `Is{Cross,Outer}ApplyJoinSupportsCondition` are set.
- Full SqlProviderFlags audit (all 76 flags reviewed against Databricks SQL): claiming CTEs, window
  functions, NULLS FIRST/LAST, EXCEPT/INTERSECT ALL, IS DISTINCT FROM, row constructors
  (Equality|In); disclaiming UPDATE...FROM and native upsert (lowered to MERGE).

**Stuff Worth Testing — done (23 smoke + 10 live tests):**
- SelectQuery inline selects, window functions (ROW_NUMBER OVER), INNER/LEFT/RIGHT/FULL/CROSS
  joins, LATERAL (correlated Take), GROUP BY, ORDER BY, LIMIT/OFFSET, EXISTS subqueries, CTEs,
  IN/NOT IN, insert/update/delete, parameterized queries, bulk copy (MultipleRows → single
  multi-row INSERT via new `DatabricksBulkCopy`; 3-row live verification), MERGE upsert.
- Live-run findings baked into the builder: Databricks MERGE rejects USING column-alias lists
  (`SupportsColumnAliasesInSource=false`) and bare VALUES sources yield unusable colN names
  (`IsValuesSyntaxSupported=false` → SELECT ... UNION ALL source form).
- Live integration coverage extended (Linq2DbSqlBitsIntegrationTests, 11 tests): SelectQuery
  inline, INNER/LEFT/RIGHT/FULL/CROSS joins, GROUP BY aggregates, EXISTS subqueries, IN/NOT IN,
  UPDATE/DELETE lifecycle, and a mapping-schema literal round-trip of every mapped type
  (bool, TIMESTAMP, DATE, DECIMAL, escaped STRING, BINARY X'..', Guid) via InlineParameters.
- Live-run mapping schema fix: Spark SQL treats '' as adjacent-literal concatenation (silently
  drops quotes); string literals must escape with backslash (\' and \\).
