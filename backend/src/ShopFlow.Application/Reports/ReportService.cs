using Microsoft.EntityFrameworkCore;
using ShopFlow.Application.Abstractions;
using ShopFlow.Application.Catalog;

namespace ShopFlow.Application.Reports;

public class RankedProductRow
{
    public string CategoryName { get; set; } = default!;
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = default!;
    public decimal Revenue { get; set; }
    public int UnitsSold { get; set; }
    public long Rnk { get; set; }
}

public class ReportService(IAppDbContext db, ICurrentUser user, CatalogCache cache)
{
    /// <summary>
    /// Raw SQL with a WINDOW FUNCTION (RANK() OVER PARTITION BY) - "top N per group" is the classic case LINQ expresses badly.
    /// Two things to notice:
    ///  * The interpolated {values} are sent as SQL PARAMETERS (SqlQuery takes a FormattableString) - never concatenate user input.
    ///  * EF global query filters DO NOT apply to raw SQL, so the tenant predicate is written by hand. Forget it => cross-tenant leak.
    /// The result is an expensive aggregate, so it is cached for 5 minutes (eventual consistency is fine for a dashboard).
    /// </summary>
    public Task<List<RankedProductRow>> TopProductsPerCategoryAsync(int top, CancellationToken ct)
    {
        top = Math.Clamp(top, 1, 20);
        var tenant = user.TenantId;
        return cache.GetOrCreateAsync(tenant, $"report:top:{tenant}:{top}", TimeSpan.FromMinutes(5), () =>
            db.Database.SqlQuery<RankedProductRow>($"""
                SELECT CategoryName, ProductId, ProductName, Revenue, UnitsSold, Rnk
                FROM (
                    SELECT c.Name AS CategoryName, p.Id AS ProductId, p.Name AS ProductName,
                           SUM(oi.UnitPrice * oi.Quantity) AS Revenue,
                           CAST(SUM(oi.Quantity) AS int)   AS UnitsSold,
                           RANK() OVER (PARTITION BY c.Id ORDER BY SUM(oi.UnitPrice * oi.Quantity) DESC) AS Rnk
                    FROM OrderItems oi
                    JOIN Orders o     ON o.Id = oi.OrderId
                    JOIN Products p   ON p.Id = oi.ProductId
                    JOIN Categories c ON c.Id = p.CategoryId
                    WHERE o.TenantId = {tenant} AND o.Status IN (1, 2, 3)   -- Paid, Shipped, Delivered
                    GROUP BY c.Id, c.Name, p.Id, p.Name
                ) ranked
                WHERE Rnk <= {top}
                ORDER BY CategoryName, Rnk
                """).ToListAsync(ct));
    }
}
