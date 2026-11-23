/* ===========================================================================================
   INDEXES & QUERY PLANS - measure, do not guess.  (Run 00_load_sample_data.sql first.)
   Turn on "Include Actual Execution Plan" (SSMS: Ctrl+M) and compare the "logical reads" this script prints.
   Rule of thumb: logical reads = pages touched = the number to drive down.
   =========================================================================================== */
USE ShopFlow;
SET NOCOUNT ON;
DECLARE @tenant uniqueidentifier = (SELECT Id FROM Tenants WHERE Code = 'acme');
DECLARE @user   uniqueidentifier = (SELECT TOP (1) Id FROM Users WHERE Email = 'load7@acme.test');
SET STATISTICS IO ON;

/* ---- 1. PAGINATION: OFFSET is O(n), keyset is O(log n) ----------------------------------------
   The admin "all orders" list needs an index that matches its ORDER BY. We drop/create it here so you can see both worlds. */
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Orders_TenantId_CreatedAt_Id' AND object_id = OBJECT_ID('Orders')) DROP INDEX IX_Orders_TenantId_CreatedAt_Id ON Orders;

PRINT '== 1a. NO matching index: every page sorts 100k rows';
SELECT TOP (20) Id, CreatedAt FROM Orders WHERE TenantId = @tenant ORDER BY CreatedAt DESC, Id DESC;

CREATE INDEX IX_Orders_TenantId_CreatedAt_Id ON Orders (TenantId, CreatedAt DESC, Id DESC);   -- same as the EF migration

PRINT '== 1b. OFFSET deep page (page 4000): reads and throws away 80,000 index rows first';
SELECT Id, CreatedAt FROM Orders WHERE TenantId = @tenant ORDER BY CreatedAt DESC, Id DESC OFFSET 80000 ROWS FETCH NEXT 20 ROWS ONLY;

DECLARE @lastAt datetime2, @lastId uniqueidentifier;
SELECT @lastAt = CreatedAt, @lastId = Id FROM Orders WHERE TenantId = @tenant ORDER BY CreatedAt DESC, Id DESC OFFSET 79999 ROWS FETCH NEXT 1 ROWS ONLY;
PRINT '== 1c. KEYSET with the cursor (CreatedAt, Id) from the previous page: seeks straight there, same rows as 1b';
SELECT TOP (20) Id, CreatedAt FROM Orders
WHERE TenantId = @tenant AND (CreatedAt < @lastAt OR (CreatedAt = @lastAt AND Id < @lastId))
ORDER BY CreatedAt DESC, Id DESC;

/* ---- 2. SARGABILITY: never wrap the indexed COLUMN in a function ------------------------------
   Both return the same rows. The first must evaluate YEAR() for every row (index SCAN, ~all pages); the second is a range the index can SEEK (a handful of pages).\n   (Runs after section 1 so IX_Orders_TenantId_CreatedAt_Id exists to be sought.) */
PRINT '== 2a. non-sargable: YEAR(CreatedAt) = 2026  (scan)';
SELECT COUNT(*) FROM Orders WHERE TenantId = @tenant AND YEAR(CreatedAt) = 2026 AND MONTH(CreatedAt) = 8;
PRINT '== 2b. sargable: half-open date range  (seek)';
SELECT COUNT(*) FROM Orders WHERE TenantId = @tenant AND CreatedAt >= '2026-08-01' AND CreatedAt < '2026-09-01';
-- The same trap: WHERE LEFT(Name, 3) = 'Lap'  vs  WHERE Name LIKE 'Lap%'.  And: WHERE Name LIKE '%top' can NEVER seek.

/* ---- 3. COVERING INDEX: kill the Key Lookup -----------------------------------------------------
   IX_Orders_TenantId_Status holds only (TenantId, Status). Asking for Total + CreatedAt forces one lookup into the clustered index
   PER ROW (random I/O). INCLUDE adds columns to the leaf level without making them part of the sort key. */
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Lab_Orders_Status_Covering') DROP INDEX IX_Lab_Orders_Status_Covering ON Orders;
PRINT '== 3a. before: seek + key lookup (or a full scan if the optimiser decides lookups are too expensive)';
SELECT Id, Total, CreatedAt FROM Orders WHERE TenantId = @tenant AND Status = 0;
CREATE INDEX IX_Lab_Orders_Status_Covering ON Orders (TenantId, Status) INCLUDE (Total, CreatedAt);
PRINT '== 3b. after: index-only seek, zero lookups';
SELECT Id, Total, CreatedAt FROM Orders WHERE TenantId = @tenant AND Status = 0;
DROP INDEX IX_Lab_Orders_Status_Covering ON Orders;          -- every index slows INSERT/UPDATE and costs space: keep only the ones you can justify

/* ---- 4. COLUMN ORDER in a composite index: equality columns first, then the sort/range column ----
   (TenantId, UserId, CreatedAt, Id) serves "my orders newest first" as a pure seek+scan with NO sort.
   The same index is useless for "WHERE CreatedAt > ..." alone (leftmost-prefix rule). */
PRINT '== 4. my newest 20 orders: index seek + 20 key lookups (for Total), and NO Sort operator';
SELECT TOP (20) Id, Total, CreatedAt FROM Orders WHERE TenantId = @tenant AND UserId = @user ORDER BY CreatedAt DESC, Id DESC;

/* ---- 5. WHAT WOULD SQL SERVER ADD?  Missing-index DMVs (suggestions, not commands - review before creating) --*/
SELECT TOP (5) CONVERT(decimal(18, 2), s.avg_total_user_cost * s.avg_user_impact * (s.user_seeks + s.user_scans)) AS Improvement,
       d.statement AS TableName, d.equality_columns, d.inequality_columns, d.included_columns
FROM sys.dm_db_missing_index_group_stats s
JOIN sys.dm_db_missing_index_groups g ON g.index_group_handle = s.group_handle
JOIN sys.dm_db_missing_index_details d ON d.index_handle = g.index_handle
ORDER BY Improvement DESC;

/* ---- 6. INDEXES NOBODY READS (they still cost on every write) -----------------------------------*/
SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, ISNULL(u.user_seeks + u.user_scans + u.user_lookups, 0) AS Reads, ISNULL(u.user_updates, 0) AS Writes
FROM sys.indexes i LEFT JOIN sys.dm_db_index_usage_stats u ON u.object_id = i.object_id AND u.index_id = i.index_id AND u.database_id = DB_ID()
WHERE OBJECTPROPERTY(i.object_id, 'IsUserTable') = 1 AND i.type_desc = 'NONCLUSTERED' ORDER BY Reads, Writes DESC;

/* ---- 7. PARAMETER SNIFFING: the plan is built for the FIRST parameter values it sees ------------
   Status = 0 matches 10% of rows, a rare status matches almost none. A plan good for one can be terrible for the other.
   Remedies, cheapest first: update statistics -> OPTION (RECOMPILE) on that statement -> OPTIMIZE FOR -> restructure the query. */
SET STATISTICS IO OFF;
DECLARE @s int = 3;
SELECT COUNT(*) FROM Orders WHERE TenantId = @tenant AND Status = @s OPTION (RECOMPILE);

/* ---- 8. IMPLICIT CONVERSION trap: comparing varchar column to nvarchar parameter converts the COLUMN (scan).
   EF Core maps string -> nvarchar and our columns are nvarchar, so we are fine. Check plans for CONVERT_IMPLICIT warnings when using Dapper/raw SQL. */