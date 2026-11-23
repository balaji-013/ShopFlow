using ShopFlow.Domain.Common;

namespace ShopFlow.Domain.Entities;

/// <summary>
/// Refresh token with ROTATION + REUSE DETECTION. We store only a hash (a DB leak must not leak sessions).
/// All tokens issued from one login share a FamilyId; replaying an already-used token revokes the whole family.
/// </summary>
public class RefreshToken : Entity
{
    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public DateTime ExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? UsedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    private RefreshToken() { }
    public RefreshToken(Guid userId, Guid familyId, string tokenHash, DateTime expiresAt)
    { UserId = userId; FamilyId = familyId; TokenHash = tokenHash; ExpiresAt = expiresAt; }

    public bool IsExpired(DateTime now) => now >= ExpiresAt;
    public void MarkUsed() => UsedAt = DateTime.UtcNow;
    public void Revoke() => RevokedAt ??= DateTime.UtcNow;
}

/// <summary>
/// TRANSACTIONAL OUTBOX: the "order placed" message is written in the SAME transaction as the order.
/// A background worker publishes it afterwards -> no lost messages, no "DB committed but broker down" gap.
/// </summary>
public class OutboxMessage : Entity
{
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }

    private OutboxMessage() { }
    public OutboxMessage(string type, string payload) { Type = type; Payload = payload; }

    public void MarkProcessed() { ProcessedAt = DateTime.UtcNow; Error = null; }
    public void MarkFailed(string error) { Attempts++; Error = error.Length > 500 ? error[..500] : error; }
}

/// <summary>Stored result of a POST made with an Idempotency-Key header, so client retries never create duplicates.</summary>
public class IdempotencyRecord
{
    public Guid UserId { get; set; }
    public string Key { get; set; } = default!;
    public string RequestHash { get; set; } = default!;   // same key + different body = client bug -> 422
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Payment-provider webhook events we have already handled. Unique EventId = "Stripe sends it twice" is harmless.</summary>
public class ProcessedWebhookEvent
{
    public string EventId { get; set; } = default!;
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog : Entity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public string Action { get; private set; } = default!;
    public string EntityType { get; private set; } = default!;
    public string EntityId { get; private set; } = default!;
    public string? Details { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    private AuditLog() { }
    public AuditLog(Guid tenantId, Guid? userId, string action, string entityType, string entityId, string? details = null)
    { TenantId = tenantId; UserId = userId; Action = action; EntityType = entityType; EntityId = entityId; Details = details; }
}

