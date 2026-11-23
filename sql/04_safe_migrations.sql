/* ===========================================================================================
   "Add a NOT NULL column to a table with 50 million rows - without downtime."
   The naive ALTER TABLE ... ADD col int NOT NULL DEFAULT 0 can be metadata-only on modern SQL Server for constants, but:
     * adding a column that must be BACKFILLED with computed values takes a long-held lock + huge log growth,
     * and the app version running DURING the deploy does not know the column exists yet.
   The safe answer is EXPAND -> MIGRATE -> CONTRACT across several deploys.
   =========================================================================================== */
USE ShopFlow;
SET NOCOUNT ON;

/* ---- STEP 1 (deploy 1: EXPAND) - add the column as NULLABLE. Metadata-only, instant, old code keeps working. ----*/
IF COL_LENGTH('Orders', 'ItemCount') IS NULL ALTER TABLE Orders ADD ItemCount int NULL;
GO   -- batch separator: the new column must exist before the next batch is COMPILED

/* ---- STEP 2 (deploy 2) - application starts WRITING the new column for every new/changed row (dual-write). ----*/

/* ---- STEP 3: BACKFILL old rows in small batches. Small transactions => short locks, log can truncate between
        batches, replicas keep up, and you can stop/resume any time. Never one giant UPDATE. ----*/
DECLARE @batch int = 5000, @rows int = 1;
WHILE @rows > 0
BEGIN
    UPDATE TOP (@batch) o SET ItemCount = (SELECT COUNT(*) FROM OrderItems i WHERE i.OrderId = o.Id)
    FROM Orders o WHERE o.ItemCount IS NULL;
    SET @rows = @@ROWCOUNT;
    -- WAITFOR DELAY '00:00:00.200';      -- in production: breathe between batches to let other work through
END

GO
/* ---- STEP 4 (deploy 3: CONTRACT) - verify there are no NULLs, then tighten the constraint. ----*/
IF NOT EXISTS (SELECT 1 FROM Orders WHERE ItemCount IS NULL)
    ALTER TABLE Orders ALTER COLUMN ItemCount int NOT NULL;
SELECT TOP (3) Id, ItemCount FROM Orders;

/* ---- Other safe-migration rules ---------------------------------------------------------------
   * RENAME a column  = add new, dual-write, backfill, switch reads, drop old (never a single deploy).
   * Big index builds: CREATE INDEX ... WITH (ONLINE = ON) on Enterprise/Azure SQL so writes are not blocked.
   * Adding a FOREIGN KEY on a big table: add WITH NOCHECK, then VALIDATE (ALTER TABLE ... WITH CHECK CHECK CONSTRAINT) off-peak.
   * Every migration must be BACKWARD COMPATIBLE with the previous app version (rolling deploys run both at once).
   * Keep migrations in source control (EF Core migrations) and run them as an explicit pipeline step, not on app start in production.
   * Test the rollback plan: "a backup you have never restored is not a backup" (see 05_backup_restore.sql). */

GO
-- cleanup of this lab so the EF model and database stay in sync:
ALTER TABLE Orders DROP COLUMN ItemCount;