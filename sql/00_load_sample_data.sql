/* ===========================================================================================
   LAB ONLY. Generates ~1,000 customers and ~100,000 orders for tenant "acme" so that indexes,
   query plans and pagination behave like a real system (60 products and 3 orders prove nothing).
   Run once after the API has created + seeded the DB:   sqlcmd -S "(localdb)\MSSQLLocalDB" -d ShopFlow -i 00_load_sample_data.sql
   Throw it away afterwards:   dotnet ef database drop   (the API recreates + reseeds it on next start)
   =========================================================================================== */
SET NOCOUNT ON;
DECLARE @tenant uniqueidentifier = (SELECT Id FROM Tenants WHERE Code = 'acme');
IF @tenant IS NULL BEGIN RAISERROR('Start the API once first so the database is migrated and seeded.', 16, 1); RETURN; END
IF EXISTS (SELECT 1 FROM Users WHERE Email LIKE 'load%@acme.test') BEGIN PRINT 'Already loaded.'; RETURN; END

-- 1) 1,000 users.  (Numbers come from cross-joining a system catalogue: a cheap "tally table".)
;WITH n AS (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT Users (Id, TenantId, Email, PasswordHash, FullName, Role, CreatedAt)
SELECT NEWID(), @tenant, CONCAT('load', i, '@acme.test'), 'x', CONCAT('Load User ', i), 0, SYSUTCDATETIME() FROM n;

DECLARE @users TABLE (rn int IDENTITY(1,1) PRIMARY KEY, Id uniqueidentifier);
INSERT @users (Id) SELECT Id FROM Users WHERE Email LIKE 'load%@acme.test';

-- 2) 100,000 orders spread over ~70 days. Status: 60% Paid, 20% Shipped, 10% Delivered, 10% Pending.
;WITH n AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
INSERT Orders (Id, TenantId, UserId, Status, Total, CreatedAt)
SELECT NEWID(), @tenant, u.Id,
       CASE WHEN i % 10 < 6 THEN 1 WHEN i % 10 < 8 THEN 2 WHEN i % 10 < 9 THEN 3 ELSE 0 END,
       0, DATEADD(MINUTE, -i, SYSUTCDATETIME())
FROM n JOIN @users u ON u.rn = (i % 1000) + 1;

-- 3) 1-3 items per order, then derive each order's total from its items.
INSERT OrderItems (Id, TenantId, OrderId, ProductId, ProductName, UnitPrice, Quantity)
SELECT NEWID(), o.TenantId, o.Id, p.Id, p.Name, p.Price, 1 + ABS(CHECKSUM(NEWID())) % 3
FROM Orders o
CROSS APPLY (SELECT TOP (1 + ABS(CHECKSUM(o.Id)) % 3) Id, Name, Price FROM Products WHERE TenantId = o.TenantId ORDER BY NEWID()) p
WHERE o.TenantId = @tenant AND o.Total = 0;

UPDATE o SET Total = x.T FROM Orders o JOIN (SELECT OrderId, SUM(UnitPrice * Quantity) AS T FROM OrderItems GROUP BY OrderId) x ON x.OrderId = o.Id WHERE o.Total = 0;

UPDATE STATISTICS Orders; UPDATE STATISTICS OrderItems;   -- the optimiser plans from statistics: refresh them after a bulk load
SELECT (SELECT COUNT(*) FROM Orders) AS Orders, (SELECT COUNT(*) FROM OrderItems) AS OrderItems, (SELECT COUNT(*) FROM Users) AS Users;