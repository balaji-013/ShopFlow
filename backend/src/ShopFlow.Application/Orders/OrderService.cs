using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ShopFlow.Application.Abstractions;
using ShopFlow.Application.Catalog;
using ShopFlow.Application.Common;
using ShopFlow.Domain.Common;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Application.Orders;

public record OrderLineRequest(Guid ProductId, int Quantity);
public record PlaceOrderRequest(List<OrderLineRequest> Items);
public record OrderItemDto(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity);
public record OrderDto(Guid Id, string Status, decimal Total, DateTime CreatedAt, DateTime? PaidAt, string? PaymentReference,
                       IReadOnlyList<OrderItemDto> Items, string Version);
public record PlacedOrder(OrderDto Order, bool Replayed);

public class PlaceOrderValidator : AbstractValidator<PlaceOrderRequest>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.Items).NotEmpty().Must(i => i.Count <= 50).WithMessage("At most 50 lines per order.");
        RuleForEach(x => x.Items).ChildRules(l => l.RuleFor(i => i.Quantity).InclusiveBetween(1, 1000));
    }
}

public class OrderService(IAppDbContext db, ICurrentUser user, CatalogCache cache, IPaymentGateway gateway,
                          IValidator<PlaceOrderRequest> validator)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ───────────────────────────── PLACE ORDER ─────────────────────────────
    public async Task<PlacedOrder> PlaceAsync(PlaceOrderRequest req, string? idempotencyKey, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(req, ct);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64)
            throw new UnprocessableException("An Idempotency-Key header (max 64 chars) is required.", "idempotency_key_required");

        // Same product listed twice? Merge, otherwise the stock check below would be per-line instead of per-product.
        var lines = req.Items.GroupBy(i => i.ProductId)
            .Select(g => new OrderLineRequest(g.Key, g.Sum(x => x.Quantity)))
            .OrderBy(l => l.ProductId)     // CONSISTENT LOCK ORDER => two orders touching {A,B} and {B,A} can never deadlock each other
            .ToList();

        var requestHash = Hash(string.Join(';', lines.Select(l => $"{l.ProductId}:{l.Quantity}")));

        var replay = await TryReplayAsync(idempotencyKey, requestHash, ct);
        if (replay is not null) return new PlacedOrder(replay, true);

        try
        {
            var dto = await CreateOrderTransactionAsync(lines, idempotencyKey, requestHash, ct);
            cache.Invalidate(user.TenantId);       // stock changed -> product pages are stale
            return new PlacedOrder(dto, false);
        }
        catch (DbUpdateException)
        {
            // A parallel request with the SAME key won the race to insert the IdempotencyRecord (composite PK). Return its result.
            var winner = await TryReplayAsync(idempotencyKey, requestHash, ct);
            if (winner is not null) return new PlacedOrder(winner, true);
            throw;
        }
    }

    private async Task<OrderDto?> TryReplayAsync(string key, string requestHash, CancellationToken ct)
    {
        var rec = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == user.UserId && r.Key == key, ct);
        if (rec is null) return null;
        if (rec.RequestHash != requestHash)
            throw new UnprocessableException("This Idempotency-Key was already used with a different request body.", "idempotency_key_reused");
        return JsonSerializer.Deserialize<OrderDto>(rec.ResponseBody, Json);
    }

    private async Task<OrderDto> CreateOrderTransactionAsync(List<OrderLineRequest> lines, string key, string requestHash, CancellationToken ct)
    {
        // With EnableRetryOnFailure, user transactions MUST run inside the execution strategy so the whole unit can be retried.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            foreach (var line in lines)
            {
                // ATOMIC conditional decrement - ONE statement:  UPDATE Products SET Stock = Stock - @q WHERE Id=@id AND Stock >= @q
                // Two buyers of the last item: SQL Server serialises the row lock, the second UPDATE matches 0 rows -> "out of stock".
                // The naive version (SELECT stock; if (stock>=q) stock -= q; SaveChanges) oversells under concurrency.
                var affected = await db.Products
                    .Where(p => p.Id == line.ProductId && p.IsActive && p.Stock >= line.Quantity)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - line.Quantity), ct);

                if (affected == 0)
                {
                    var exists = await db.Products.AnyAsync(p => p.Id == line.ProductId && p.IsActive, ct);
                    if (!exists) throw new NotFoundException($"Product {line.ProductId}");
                    throw new ConflictException($"Insufficient stock for product {line.ProductId}.", "insufficient_stock");
                }
            }

            // We hold row locks on these products until commit, so price/name read here cannot change under us.
            var ids = lines.Select(l => l.ProductId).ToList();
            var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

            var order = Order.Create(user.TenantId, user.UserId,
                lines.Select(l => (l.ProductId, products[l.ProductId].Name, products[l.ProductId].Price, l.Quantity)));
            db.Orders.Add(order);

            // OUTBOX: same transaction as the order. Delivery (SignalR push, e-mail) happens later in a background worker.
            db.OutboxMessages.Add(new OutboxMessage("OrderPlaced",
                JsonSerializer.Serialize(new { orderId = order.Id, userId = user.UserId, total = order.Total }, Json)));
            db.AuditLogs.Add(new AuditLog(user.TenantId, user.UserId, "order.placed", nameof(Order), order.Id.ToString(), $"total={order.Total}"));

            var dto = ToDto(order);
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                UserId = user.UserId, Key = key, RequestHash = requestHash, StatusCode = 201,
                ResponseBody = JsonSerializer.Serialize(dto, Json)
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return dto;
        });
    }

    // ───────────────────────────── READ ─────────────────────────────
    /// <summary>
    /// AUTHORIZATION AT THE DATA LAYER: the ownership check is part of the query, not an if-statement a developer can forget.
    /// Someone else's order returns 404 (not 403) so we do not even confirm that the id exists.
    /// The tenant filter is applied on top, automatically, by the EF global query filter.
    /// </summary>
    private IQueryable<Order> Visible() => db.Orders.Where(o => user.IsAdmin || o.UserId == user.UserId);

    public async Task<OrderDto> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await Visible().AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct)
                    ?? throw new NotFoundException("Order");
        return ToDto(order);
    }

    public async Task<CursorPage<OrderDto>> ListAsync(string? cursor, int limit, bool all, CancellationToken ct)
    {
        if (all && !user.IsAdmin) throw new ForbiddenAppException();
        limit = Math.Clamp(limit, 1, 100);

        var q = (all ? db.Orders : db.Orders.Where(o => o.UserId == user.UserId)).AsNoTracking();
        if (Cursor.TryDecode(cursor, out var at, out var id))
            q = q.Where(o => o.CreatedAt < at || (o.CreatedAt == at && o.Id.CompareTo(id) < 0));
        else if (!string.IsNullOrEmpty(cursor))
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("cursor", "Cursor is invalid.")]);

        // Include() of a collection => one extra query via AsSplitQuery avoids the cartesian explosion of a big JOIN.
        var rows = await q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Include(o => o.Items).AsSplitQuery().Take(limit + 1).ToListAsync(ct);

        var page = rows.Take(limit).Select(ToDto).ToList();
        var next = rows.Count > limit ? Cursor.Encode(rows[limit - 1].CreatedAt, rows[limit - 1].Id) : null;
        return new CursorPage<OrderDto>(page, next);
    }

    // ───────────────────────────── STATE CHANGES ─────────────────────────────
    public async Task<PaymentIntent> StartPaymentAsync(Guid id, CancellationToken ct)
    {
        var order = await Visible().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Order");
        if (order.Status != OrderStatus.Pending) throw new UnprocessableException("Only pending orders can be paid.", "invalid_transition");
        return await gateway.CreateIntentAsync(order.Id, order.Total, ct);   // resilient HttpClient (timeout/retry/circuit breaker) in Infrastructure
    }

    public async Task<OrderDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var dto = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();   // a retried attempt must start from a clean tracker
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var order = await Visible().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Order");
            try { order.Cancel(); } catch (DomainException e) { throw new UnprocessableException(e.Message, e.Code); }

            foreach (var item in order.Items.OrderBy(i => i.ProductId))
                await db.Products.Where(p => p.Id == item.ProductId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + item.Quantity), ct);   // give the stock back

            db.OutboxMessages.Add(new OutboxMessage("OrderCancelled",
                JsonSerializer.Serialize(new { orderId = order.Id, userId = order.UserId }, Json)));
            db.AuditLogs.Add(new AuditLog(user.TenantId, user.UserId, "order.cancelled", nameof(Order), id.ToString()));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new ConflictException("The order changed (e.g. it was just paid). Reload and retry.", "concurrency_conflict"); }
            await tx.CommitAsync(ct);
            return ToDto(order);
        });
        cache.Invalidate(user.TenantId);
        return dto;
    }

    public async Task<OrderDto> AdvanceAsync(Guid id, OrderStatus target, CancellationToken ct)
    {
        if (!user.IsAdmin) throw new ForbiddenAppException();
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Order");
        try
        {
            switch (target)
            {
                case OrderStatus.Shipped: order.Ship(); break;
                case OrderStatus.Delivered: order.Deliver(); break;
                default: throw new UnprocessableException("Unsupported target status.", "invalid_transition");
            }
        }
        catch (DomainException e) { throw new UnprocessableException(e.Message, e.Code); }

        db.OutboxMessages.Add(new OutboxMessage("OrderStatusChanged",
            JsonSerializer.Serialize(new { orderId = id, userId = order.UserId, status = order.Status.ToString() }, Json)));
        db.AuditLogs.Add(new AuditLog(user.TenantId, user.UserId, $"order.{target.ToString().ToLower()}", nameof(Order), id.ToString()));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The order changed. Reload and retry.", "concurrency_conflict"); }
        return ToDto(order);
    }

    // ───────────────────────────── helpers ─────────────────────────────
    public static OrderDto ToDto(Order o) => new(o.Id, o.Status.ToString(), o.Total, o.CreatedAt, o.PaidAt, o.PaymentReference,
        o.Items.Select(i => new OrderItemDto(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity)).ToList(),
        Convert.ToBase64String(o.RowVersion));

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}


