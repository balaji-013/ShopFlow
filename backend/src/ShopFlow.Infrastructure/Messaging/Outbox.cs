using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopFlow.Application.Abstractions;
using ShopFlow.Domain.Entities;
using ShopFlow.Infrastructure.Persistence;

namespace ShopFlow.Infrastructure.Messaging;

/// <summary>
/// Reads the outbox and "publishes". Safe to run on several app instances at once:
///  * UPDLOCK + READPAST => each instance locks the rows it picked, and other instances SKIP locked rows instead of waiting.
///  * Delivery is AT-LEAST-ONCE (a crash after publish, before commit, re-sends), so consumers must be idempotent.
///    Here the consumer is "tell the browser to refetch this order" - naturally idempotent.
/// </summary>
public class OutboxDispatcher(AppDbContext db, IRealtimeNotifier notifier, ILogger<OutboxDispatcher> log)
{
    private const int MaxAttempts = 5;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var batch = await db.OutboxMessages
                .FromSqlInterpolated($"SELECT TOP ({batchSize}) * FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK) WHERE ProcessedAt IS NULL AND Attempts < {MaxAttempts} ORDER BY CreatedAt")
                .ToListAsync(ct);

            foreach (var m in batch)
            {
                try { await DispatchAsync(m, ct); m.MarkProcessed(); }
                catch (Exception ex) { log.LogError(ex, "Outbox message {Id} ({Type}) failed", m.Id, m.Type); m.MarkFailed(ex.Message); }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return batch.Count;
        });
    }

    private async Task DispatchAsync(OutboxMessage m, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(m.Payload);
        var root = doc.RootElement;
        var userId = root.TryGetProperty("userId", out var u) ? u.GetGuid() : (Guid?)null;
        var orderId = root.TryGetProperty("orderId", out var o) ? o.GetGuid() : (Guid?)null;

        switch (m.Type)
        {
            case "OrderPlaced":
            case "OrderPaid":
            case "OrderCancelled":
            case "OrderStatusChanged":
                var status = m.Type switch
                {
                    "OrderPlaced" => "Pending", "OrderPaid" => "Paid", "OrderCancelled" => "Cancelled",
                    _ => root.GetProperty("status").GetString()!
                };
                log.LogInformation("[email stub] order {OrderId} -> {Status}", orderId, status);   // real life: send e-mail via a provider here
                if (userId is { } id) await notifier.NotifyUserAsync(id, "order.updated", new { orderId, status }, ct);
                break;
            case "RefundRequired":
                log.LogWarning("REFUND REQUIRED for order {OrderId}: {Payload}", orderId, m.Payload);
                break;
            default:
                throw new InvalidOperationException($"Unknown outbox message type {m.Type}");
        }
    }
}

public class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
        do
        {
            try
            {
                int n;
                do
                {
                    using var scope = scopes.CreateScope();           // BackgroundService is a singleton; DbContext is scoped -> new scope per batch
                    n = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().ProcessBatchAsync(20, stoppingToken);
                } while (n == 20 && !stoppingToken.IsCancellationRequested);    // keep draining while there is a backlog
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogError(ex, "Outbox loop failed; will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }
}

