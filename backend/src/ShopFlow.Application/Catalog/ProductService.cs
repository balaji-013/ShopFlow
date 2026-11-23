using System.Collections.Concurrent;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using ShopFlow.Application.Abstractions;
using ShopFlow.Application.Common;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Application.Catalog;

public record ProductDto(Guid Id, string Sku, string Name, string Description, decimal Price, int Stock,
                         Guid CategoryId, string CategoryName, string Version, DateTime CreatedAt);
public record CategoryDto(Guid Id, string Name);
public record ProductQuery(string? Search, Guid? CategoryId, string? Cursor, int Limit = 20);
public record CreateProductRequest(string Sku, string Name, string Description, decimal Price, int Stock, Guid CategoryId);
public record UpdateProductRequest(string Name, string Description, decimal Price, Guid CategoryId, string Version);
public record AdjustStockRequest(int Delta);

public class CreateProductValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, false);
        RuleFor(x => x.Stock).GreaterThanOrEqualTo(0);
    }
}

/// <summary>
/// CACHE-ASIDE with targeted invalidation. Entries are tied to a per-tenant change token: any write calls Invalidate(tenant)
/// and every cached page of that tenant disappears at once. TTL is a safety net, not the invalidation strategy.
/// In a multi-instance deployment this becomes Redis (IDistributedCache / HybridCache) - see docs/03-system-design.md.
/// </summary>
public class CatalogCache(IMemoryCache cache)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

    public async Task<T> GetOrCreateAsync<T>(Guid tenantId, string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        if (cache.TryGetValue(key, out T? hit) && hit is not null) return hit;
        var value = await factory();
        var cts = _tokens.GetOrAdd(tenantId, _ => new CancellationTokenSource());
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }
            .AddExpirationToken(new CancellationChangeToken(cts.Token)));
        return value;
    }

    public void Invalidate(Guid tenantId)
    {
        if (_tokens.TryRemove(tenantId, out var cts)) { cts.Cancel(); cts.Dispose(); }
    }
}

public class ProductService(IAppDbContext db, ICurrentUser user, CatalogCache cache, IValidator<CreateProductRequest> createValidator)
{
    private const int MaxLimit = 100;

    public async Task<CursorPage<ProductDto>> ListAsync(ProductQuery q, CancellationToken ct)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);          // never let a client ask for 1,000,000 rows
        var hasCursor = Cursor.TryDecode(q.Cursor, out var cAt, out var cId);
        if (!string.IsNullOrEmpty(q.Cursor) && !hasCursor)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("cursor", "Cursor is invalid.")]);

        // Only the hot path (first page, no search) is cached; everything else is too varied to be worth it.
        var cacheable = !hasCursor && string.IsNullOrWhiteSpace(q.Search);
        var key = $"products:{user.TenantId}:{q.CategoryId}:{limit}";

        Task<CursorPage<ProductDto>> Load() => QueryAsync(q, limit, hasCursor, cAt, cId, ct);
        return cacheable ? await cache.GetOrCreateAsync(user.TenantId, key, TimeSpan.FromSeconds(30), Load) : await Load();
    }

    private async Task<CursorPage<ProductDto>> QueryAsync(ProductQuery q, int limit, bool hasCursor, DateTime cAt, Guid cId, CancellationToken ct)
    {
        var query = db.Products.AsNoTracking().Where(p => p.IsActive);   // AsNoTracking: read-only queries skip change-tracking overhead

        if (q.CategoryId is { } cat) query = query.Where(p => p.CategoryId == cat);

        // Prefix LIKE ('term%') is SARGABLE: it can use the index on Name. '%term%' cannot (full scan) - use Full-Text Search for that.
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(p => EF.Functions.Like(p.Name, EscapeLike(term) + "%", "\\"));
        }

        if (hasCursor)
            query = query.Where(p => p.CreatedAt < cAt || (p.CreatedAt == cAt && p.Id.CompareTo(cId) < 0));

        // Fetch limit+1: the extra row tells us whether a next page exists, without a second COUNT query.
        var rows = await query
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Take(limit + 1)
            .Select(p => new { p.Id, p.Sku, p.Name, p.Description, p.Price, p.Stock, p.CategoryId, CategoryName = p.Category!.Name, p.RowVersion, p.CreatedAt })
            .ToListAsync(ct);

        var page = rows.Take(limit)
            .Select(r => new ProductDto(r.Id, r.Sku, r.Name, r.Description, r.Price, r.Stock, r.CategoryId, r.CategoryName,
                                        Convert.ToBase64String(r.RowVersion), r.CreatedAt)).ToList();
        var next = rows.Count > limit ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;
        return new CursorPage<ProductDto>(page, next);
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken ct) =>
        await db.Categories.AsNoTracking().OrderBy(c => c.Name).Select(c => new CategoryDto(c.Id, c.Name)).ToListAsync(ct);

    public async Task<ProductDto> GetAsync(Guid id, CancellationToken ct) =>
        await db.Products.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Description, p.Price, p.Stock, p.CategoryId, p.Category!.Name,
                                        Convert.ToBase64String(p.RowVersion), p.CreatedAt))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Product");

    public async Task<ProductDto> CreateAsync(CreateProductRequest r, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(r, ct);
        if (!await db.Categories.AnyAsync(c => c.Id == r.CategoryId, ct)) throw new NotFoundException("Category");
        if (await db.Products.AnyAsync(p => p.Sku == r.Sku, ct)) throw new ConflictException("SKU already exists.", "sku_taken");

        var p = new Product(user.TenantId, r.CategoryId, r.Sku.Trim(), r.Name.Trim(), r.Description, r.Price, r.Stock);
        db.Products.Add(p);
        db.AuditLogs.Add(new AuditLog(user.TenantId, user.UserId, "product.created", nameof(Product), p.Id.ToString(), $"sku={p.Sku}"));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("SKU already exists.", "sku_taken"); }
        cache.Invalidate(user.TenantId);
        return await GetAsync(p.Id, ct);
    }

    /// <summary>
    /// OPTIMISTIC CONCURRENCY: the client sends the Version (rowversion) it last saw. If someone else saved in between,
    /// the UPDATE ... WHERE RowVersion = @old touches 0 rows, EF throws, and we answer 409 instead of silently overwriting.
    /// </summary>
    public async Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest r, CancellationToken ct)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product");
        if (!TryFromBase64(r.Version, out var version)) throw new ValidationException([new FluentValidation.Results.ValidationFailure("version", "Version is invalid.")]);

        db.Entry(p).Property(x => x.RowVersion).OriginalValue = version;
        p.Update(r.Name.Trim(), r.Description, r.Price, r.CategoryId);
        db.AuditLogs.Add(new AuditLog(user.TenantId, user.UserId, "product.updated", nameof(Product), id.ToString(), $"price={r.Price}"));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The product was modified by someone else. Reload and retry.", "concurrency_conflict"); }
        cache.Invalidate(user.TenantId);
        return await GetAsync(id, ct);
    }

    /// <summary>Relative stock change done IN SQL (Stock = Stock + @delta) - never read-modify-write in C#, that loses concurrent updates.</summary>
    public async Task<ProductDto> AdjustStockAsync(Guid id, AdjustStockRequest r, CancellationToken ct)
    {
        var rows = await db.Products.Where(p => p.Id == id && p.Stock + r.Delta >= 0)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + r.Delta), ct);
        if (rows == 0)
        {
            if (!await db.Products.AnyAsync(p => p.Id == id, ct)) throw new NotFoundException("Product");
            throw new UnprocessableException("Stock cannot go below zero.", "invalid_stock");
        }
        db.AuditLogs.Add(new AuditLog(user.TenantId, user.UserId, "product.stock_adjusted", nameof(Product), id.ToString(), $"delta={r.Delta}"));
        await db.SaveChangesAsync(ct);
        cache.Invalidate(user.TenantId);
        return await GetAsync(id, ct);
    }

    private static bool TryFromBase64(string s, out byte[] bytes)
    {
        try { bytes = Convert.FromBase64String(s); return bytes.Length > 0; } catch { bytes = []; return false; }
    }
}
