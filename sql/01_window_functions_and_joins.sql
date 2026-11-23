/* ===========================================================================================
   SQL INTERVIEW CLASSICS, answered against the ShopFlow schema.  (Run 00_load_sample_data.sql first.)
   =========================================================================================== */
USE ShopFlow;
DECLARE @tenant uniqueidentifier = (SELECT Id FROM Tenants WHERE Code = 'acme');

/* ---- Q1. "Top 3 orders per customer" -------------------------------------------------------
   ROW_NUMBER() numbers rows inside each PARTITION. Filter in an outer query/CTE (window functions cannot be used in WHERE).
   Tie-break by Id so the result is deterministic. */
;WITH ranked AS (
    SELECT o.UserId, o.Id AS OrderId, o.Total, o.CreatedAt,
           ROW_NUMBER() OVER (PARTITION BY o.UserId ORDER BY o.Total DESC, o.Id) AS rn
    FROM Orders o WHERE o.TenantId = @tenant AND o.Status IN (1, 2, 3)
)
SELECT TOP (15) u.Email, r.OrderId, r.Total, r.rn FROM ranked r JOIN Users u ON u.Id = r.UserId WHERE r.rn <= 3 ORDER BY u.Email, r.rn;

/* ---- Q2. ROW_NUMBER vs RANK vs DENSE_RANK  (the follow-up every interviewer asks) ----------
   Values 100,100,90 ->  ROW_NUMBER 1,2,3   RANK 1,1,3 (gap)   DENSE_RANK 1,1,2 (no gap)  */
SELECT v, ROW_NUMBER() OVER (ORDER BY v DESC) AS row_number_, RANK() OVER (ORDER BY v DESC) AS rank_, DENSE_RANK() OVER (ORDER BY v DESC) AS dense_rank_
FROM (VALUES (100), (100), (90)) t(v);

/* ---- Q3. Second-highest price per category (without TOP/OFFSET tricks) ----------------------*/
;WITH d AS (SELECT c.Name AS Category, p.Name, p.Price, DENSE_RANK() OVER (PARTITION BY p.CategoryId ORDER BY p.Price DESC) AS dr
            FROM Products p JOIN Categories c ON c.Id = p.CategoryId WHERE p.TenantId = @tenant)
SELECT Category, Name, Price FROM d WHERE dr = 2;

/* ---- Q4. Running total + share of total (aggregate window: no GROUP BY collapse) -----------*/
;WITH daily AS (SELECT CAST(CreatedAt AS date) AS Day, SUM(Total) AS Revenue FROM Orders WHERE TenantId = @tenant AND Status IN (1, 2, 3) GROUP BY CAST(CreatedAt AS date))
SELECT TOP (7) Day, Revenue,
       SUM(Revenue) OVER (ORDER BY Day ROWS UNBOUNDED PRECEDING) AS RunningRevenue,
       CAST(100.0 * Revenue / SUM(Revenue) OVER () AS decimal(5, 2)) AS PctOfAll
FROM daily ORDER BY Day;

/* ---- Q5. Time since the customer's previous order (LAG) -----------------------------------*/
SELECT TOP (10) UserId, CreatedAt,
       DATEDIFF(MINUTE, LAG(CreatedAt) OVER (PARTITION BY UserId ORDER BY CreatedAt), CreatedAt) AS MinutesSincePrevious
FROM Orders WHERE TenantId = @tenant ORDER BY UserId, CreatedAt;

/* ---- Q6. Customers who NEVER ordered: NOT EXISTS beats NOT IN ------------------------------
   NOT IN returns nothing at all if the subquery produces a single NULL. NOT EXISTS is NULL-safe and usually plans as an anti-semi-join. */
SELECT COUNT(*) AS CustomersWithoutOrders FROM Users u
WHERE u.TenantId = @tenant AND u.Role = 0 AND NOT EXISTS (SELECT 1 FROM Orders o WHERE o.UserId = u.Id);

/* ---- Q7. Products never sold: LEFT JOIN ... IS NULL ----------------------------------------*/
SELECT p.Sku, p.Name FROM Products p LEFT JOIN OrderItems oi ON oi.ProductId = p.Id WHERE p.TenantId = @tenant AND oi.Id IS NULL;

/* ---- Q8. WHERE vs HAVING: WHERE filters rows BEFORE grouping, HAVING filters groups AFTER ---*/
SELECT UserId, COUNT(*) AS Orders, SUM(Total) AS Spent
FROM Orders WHERE TenantId = @tenant AND Status IN (1, 2, 3)      -- row filter (can use an index)
GROUP BY UserId HAVING COUNT(*) >= 100                              -- group filter
ORDER BY Spent DESC OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY;

/* ---- Q9. Remove duplicates, keep the newest: ROW_NUMBER + DELETE through a CTE -------------*/
-- ;WITH d AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY Email ORDER BY CreatedAt DESC) AS rn FROM SomeTable) DELETE FROM d WHERE rn > 1;

/* ---- Q10. Pivot-style conditional aggregation: orders per status in ONE pass ---------------*/
SELECT SUM(CASE WHEN Status = 0 THEN 1 ELSE 0 END) AS Pending, SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END) AS Paid,
       SUM(CASE WHEN Status = 2 THEN 1 ELSE 0 END) AS Shipped, SUM(CASE WHEN Status = 3 THEN 1 ELSE 0 END) AS Delivered
FROM Orders WHERE TenantId = @tenant;