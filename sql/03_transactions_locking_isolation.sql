/* ===========================================================================================
   TRANSACTIONS, LOCKING, ISOLATION - "Two users buy the last item at once. What happens?"
   Open TWO query windows (Session A / Session B) and run the numbered steps alternately.
   The same scenarios are automated in backend/tests/ShopFlow.IntegrationTests (OrderTests).
   =========================================================================================== */
USE ShopFlow;

/* ---- SETUP: a product with exactly 1 unit left ------------------------------------------------*/
DECLARE @pid uniqueidentifier = (SELECT TOP (1) Id FROM Products WHERE Sku = 'AC-0001');
UPDATE Products SET Stock = 1 WHERE Id = @pid;
SELECT Sku, Stock FROM Products WHERE Id = @pid;

/* ============ SCENARIO 1: LOST UPDATE (the naive read-modify-write) ============================
   Session A                                         Session B
   BEGIN TRAN;
   SELECT @s = Stock ...  -- sees 1
                                                     BEGIN TRAN;
                                                     SELECT @s = Stock ... -- ALSO sees 1 (READ COMMITTED takes no lasting read lock)
   UPDATE Products SET Stock = @s - 1 ...            
   COMMIT;                                           UPDATE Products SET Stock = @s - 1 ...
                                                     COMMIT;     -- both sold the last item; stock = 0 but 2 orders exist = OVERSOLD
   Fix options: (a) atomic conditional UPDATE  (b) UPDLOCK/serialisable read  (c) optimistic rowversion check + retry  */

/* ============ SCENARIO 2: THE FIX WE USE - one atomic statement ================================
   Whoever gets the row lock first decrements; the second UPDATE re-evaluates WHERE after the lock is released and matches 0 rows. */
-- Run in Session A then Session B. Check @@ROWCOUNT: 1 = you got it, 0 = sold out.
DECLARE @pid2 uniqueidentifier = (SELECT TOP (1) Id FROM Products WHERE Sku = 'AC-0001');
BEGIN TRAN;
    UPDATE Products SET Stock = Stock - 1 WHERE Id = @pid2 AND Stock >= 1;
    SELECT @@ROWCOUNT AS RowsAffected;          -- Session A: 1.   Session B (while A still open): BLOCKS, then returns 0 after A commits
    -- WAITFOR DELAY '00:00:10';                  -- uncomment in Session A to hold the lock long enough to see Session B wait
COMMIT;
-- The CHECK constraint CK_Products_Stock_NonNegative is the safety net if someone later writes a buggy UPDATE.

/* ============ SCENARIO 3: PESSIMISTIC alternative - lock on read =================================
   UPDLOCK = "I intend to update this row": others may read, but nobody else may take UPDLOCK -> read-then-write becomes safe. */
-- BEGIN TRAN;
--   DECLARE @s int = (SELECT Stock FROM Products WITH (UPDLOCK, ROWLOCK) WHERE Id = @pid2);
--   IF @s >= 1 UPDATE Products SET Stock = @s - 1 WHERE Id = @pid2;
-- COMMIT;

/* ============ SCENARIO 4: OPTIMISTIC concurrency with rowversion (what ProductService.UpdateAsync does) ======
   Read the row + its RowVersion, let the user think for a minute, then write only if the version is unchanged. */
DECLARE @ver binary(8) = (SELECT RowVersion FROM Products WHERE Id = @pid2);
UPDATE Products SET Price = Price + 1 WHERE Id = @pid2 AND RowVersion = @ver;
SELECT @@ROWCOUNT AS FirstWriterWins;           -- 1
UPDATE Products SET Price = Price + 1 WHERE Id = @pid2 AND RowVersion = @ver;
SELECT @@ROWCOUNT AS SecondWriterRejected;      -- 0  -> EF throws DbUpdateConcurrencyException -> API answers 409

/* ============ ISOLATION LEVELS cheat-sheet ======================================================
   Level               Dirty read  Non-repeatable read  Phantom   Notes
   READ UNCOMMITTED    yes         yes                  yes       = NOLOCK. Can read data that is later rolled back. Avoid for money.
   READ COMMITTED      no          yes                  yes       SQL Server default: readers BLOCK on writers' locks.
   READ COMMITTED SNAPSHOT (RCSI)  no  yes              yes       Same level, but readers see the last committed version (row versioning): readers never block writers.
   REPEATABLE READ     no          no                   yes       Holds shared locks until commit.
   SNAPSHOT            no          no                   no*       Transaction sees a consistent point-in-time snapshot; write-write conflicts raise error 3960.
   SERIALIZABLE        no          no                   no        Range locks; safest, lowest concurrency, deadlock-prone.
   Recommendation for web apps: turn on RCSI (below) and keep READ COMMITTED + atomic statements + rowversion. */
-- ALTER DATABASE ShopFlow SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
SELECT name, is_read_committed_snapshot_on, snapshot_isolation_state_desc FROM sys.databases WHERE name = 'ShopFlow';

/* ============ DEADLOCK: how it happens and how we avoid it =======================================
   Session A: UPDATE ProductA ...   then   UPDATE ProductB ...
   Session B: UPDATE ProductB ...   then   UPDATE ProductA ...     -> each waits for the other: SQL Server kills one ("deadlock victim", error 1205).
   OUR FIX: OrderService sorts lines by ProductId, so every transaction takes locks in the SAME order -> no cycle is possible.
   Second line of defence: EnableRetryOnFailure retries the whole transaction when 1205 still occurs.
   To see deadlocks in production: the system_health Extended Events session (xml_deadlock_report) or trace flag 1222. */

/* ============ ACID in one sentence each =========================================================
   Atomicity   : order + stock decrement + outbox row + idempotency record commit together or not at all.
   Consistency : constraints (CHECK, FK, UNIQUE) hold before and after every transaction.
   Isolation   : concurrent transactions do not see each other's half-finished work (to the degree of the isolation level).
   Durability  : once COMMIT returns, the change survives a crash (write-ahead log flushed). */