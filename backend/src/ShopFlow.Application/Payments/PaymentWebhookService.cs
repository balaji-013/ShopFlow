using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShopFlow.Application.Abstractions;
using ShopFlow.Application.Common;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Application.Payments;

/// <summary>
/// WEBHOOK HANDLER. Three rules every webhook handler must obey:
///  1. VERIFY the signature (anyone can POST to a public URL).  2. Be IDEMPOTENT (providers deliver "at least once").
///  3. Answer fast and do slow work asynchronously (outbox).
/// </summary>
public class PaymentWebhookService(IAppDbContext db, IWebhookSignatureVerifier verifier, ILogger<PaymentWebhookService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private record WebhookData(Guid OrderId, string Reference);
    private record WebhookEvent(string Id, string Type, WebhookData Data);

    public async Task HandleAsync(string rawBody, string? signature, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(signature) || !verifier.IsValid(rawBody, signature))
            throw new UnauthorizedAppException("Invalid webhook signature.");

        var evt = JsonSerializer.Deserialize<WebhookEvent>(rawBody, Json) ?? throw new UnprocessableException("Malformed event.", "bad_event");
        if (evt.Type != "payment.succeeded") return;                       // acknowledge events we do not care about

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                if (await db.ProcessedWebhookEvents.AnyAsync(e => e.EventId == evt.Id, ct)) { log.LogInformation("Duplicate webhook {EventId} ignored", evt.Id); return; }

                // No logged-in user here => the tenant query filter would match nothing. A webhook is a system actor, so we opt out
                // explicitly (and visibly) with IgnoreQueryFilters. Never do this in code reachable by normal users.
                var order = await db.Orders.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == evt.Data.OrderId, ct);
                if (order is null) { log.LogWarning("Webhook {EventId} references unknown order {OrderId}", evt.Id, evt.Data.OrderId); }
                else if (order.Status == OrderStatus.Pending)
                {
                    order.MarkPaid(evt.Data.Reference);
                    db.OutboxMessages.Add(new OutboxMessage("OrderPaid",
                        JsonSerializer.Serialize(new { orderId = order.Id, userId = order.UserId, total = order.Total }, Json)));
                    db.AuditLogs.Add(new AuditLog(order.TenantId, null, "order.paid", nameof(Order), order.Id.ToString(), $"event={evt.Id}"));
                }
                else if (order.Status == OrderStatus.Cancelled)
                {
                    // Customer paid an order we cancelled: money must go back. Raise a message for a human/refund worker.
                    db.OutboxMessages.Add(new OutboxMessage("RefundRequired",
                        JsonSerializer.Serialize(new { orderId = order.Id, reference = evt.Data.Reference }, Json)));
                }

                db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { EventId = evt.Id });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.ProcessedWebhookEvents.AsNoTracking().AnyAsync(e => e.EventId == evt.Id, ct)) throw;
            log.LogInformation("Webhook {EventId} was processed concurrently by another request", evt.Id);   // PK violation = duplicate, which is fine
        }
    }
}

