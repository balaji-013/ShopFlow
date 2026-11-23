using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Application.Abstractions;

/// <summary>
/// DEPENDENCY INVERSION: use-cases depend on this interface, Infrastructure implements it with EF Core + SQL Server.
/// (We expose DbSets directly: wrapping EF in a generic repository would only hide IQueryable and add no value.)
/// </summary>
public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<User> Users { get; }
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderItem> OrderItems { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; }
    DbSet<AuditLog> AuditLogs { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }
    EntityEntry<T> Entry<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Who is calling? Populated from the validated JWT. TenantId drives the global tenant query filter.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    Guid TenantId { get; }
    bool IsAdmin { get; }
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password, out bool needsRehash);
}

public record AccessToken(string Value, int ExpiresInSeconds);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);
    (string Raw, string Hash) NewRefreshToken();
    string HashToken(string raw);
    TimeSpan RefreshTokenLifetime { get; }
}

public record PaymentIntent(string Reference, string Provider);

/// <summary>External dependency behind an interface => unit tests never hit the real payment provider.</summary>
public interface IPaymentGateway
{
    Task<PaymentIntent> CreateIntentAsync(Guid orderId, decimal amount, CancellationToken ct);
}

/// <summary>Push to a connected browser (SignalR in the Api project).</summary>
public interface IRealtimeNotifier
{
    Task NotifyUserAsync(Guid userId, string eventName, object payload, CancellationToken ct);
}

public interface IWebhookSignatureVerifier
{
    bool IsValid(string rawBody, string signatureHeader);
    string Sign(string rawBody, DateTimeOffset timestamp);
}


