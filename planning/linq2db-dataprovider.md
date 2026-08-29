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
