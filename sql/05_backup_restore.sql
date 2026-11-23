/* ===========================================================================================
   BACKUP & POINT-IN-TIME RESTORE - "Recover this table to its state three hours ago."
   Requires FULL recovery model (so the transaction log is kept) and a regular log backup chain.
   Run on a disposable SQL Server instance / container, not on production. LocalDB does not support all options.
   =========================================================================================== */

-- 0. Recovery model decides what you CAN restore.   SIMPLE = log auto-truncated, restore only to the last full/diff backup.
--    FULL = log retained until backed up -> point-in-time restore to any moment covered by log backups.
-- ALTER DATABASE ShopFlow SET RECOVERY FULL;

-- 1. The chain (schedule these: full weekly, differential daily, LOG every 5-15 min => your RPO equals the log interval):
-- BACKUP DATABASE ShopFlow TO DISK = 'D:\backup\ShopFlow_full.bak'  WITH COMPRESSION, CHECKSUM, INIT;
-- BACKUP DATABASE ShopFlow TO DISK = 'D:\backup\ShopFlow_diff.bak'  WITH DIFFERENTIAL, COMPRESSION, CHECKSUM;
-- BACKUP LOG      ShopFlow TO DISK = 'D:\backup\ShopFlow_log1.trn'  WITH COMPRESSION, CHECKSUM;

-- 2. Disaster 3 hours ago: someone ran DELETE without WHERE. NEVER restore over production. Restore a COPY side by side:
-- RESTORE DATABASE ShopFlow_Recovered FROM DISK = 'D:\backup\ShopFlow_full.bak'
--   WITH MOVE 'ShopFlow' TO 'D:\data\ShopFlow_Recovered.mdf', MOVE 'ShopFlow_log' TO 'D:\data\ShopFlow_Recovered.ldf', NORECOVERY;
-- RESTORE DATABASE ShopFlow_Recovered FROM DISK = 'D:\backup\ShopFlow_diff.bak' WITH NORECOVERY;
-- RESTORE LOG      ShopFlow_Recovered FROM DISK = 'D:\backup\ShopFlow_log1.trn' WITH STOPAT = '2026-10-13T05:00:00', RECOVERY;

-- 3. Copy only the damaged rows back (INSERT ... SELECT from ShopFlow_Recovered.dbo.Orders) after reviewing them. Drop the copy.

-- 4. PROVE the backup works - this is the only test that counts:
-- RESTORE VERIFYONLY FROM DISK = 'D:\backup\ShopFlow_full.bak' WITH CHECKSUM;     -- cheap sanity check
-- Schedule a REAL restore to a scratch server weekly and run DBCC CHECKDB against it.

-- Vocabulary:  RPO = how much data you can lose (log backup interval).  RTO = how long recovery takes (restore size/speed).
-- Azure SQL / managed services give point-in-time restore for free, but you still must know the RPO/RTO you are getting.
SELECT name, recovery_model_desc FROM sys.databases WHERE name = 'ShopFlow';