using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ShopFlow.Application.Abstractions;
using ShopFlow.Domain.Common;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser) : DbContext(options), IAppDbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ── MULTI-TENANCY (shared database, shared schema, TenantId column) ──
        // Every query on these entities silently gets "WHERE TenantId = @current". Forgetting a Where() can no longer leak data.
        // Trade-off vs schema-per-tenant / DB-per-tenant: cheapest to run, weakest isolation -> pair with SQL Server Row-Level Security in prod.
        TenantFilter<Category>(b); TenantFilter<Product>(b); TenantFilter<Order>(b); TenantFilter<OrderItem>(b);
        TenantFilter<AuditLog>(b);

        b.Entity<Tenant>(e => { e.HasIndex(x => x.Code).IsUnique(); e.Property(x => x.Code).HasMaxLength(50); e.Property(x => x.Name).HasMaxLength(120); });

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();                       // the real guard against duplicate registration
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.FullName).HasMaxLength(120);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Category>(e => { e.Property(x => x.Name).HasMaxLength(100); e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique(); });

        b.Entity<Product>(e =>
        {
            e.Property(x => x.Sku).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Price).HasPrecision(18, 2);              // never float/double for money
            e.Property(x => x.RowVersion).IsRowVersion();              // optimistic concurrency token
            e.ToTable(t => t.HasCheckConstraint("CK_Products_Stock_NonNegative", "[Stock] >= 0"));   // last line of defence: DB refuses negative stock
            e.ToTable(t => t.HasCheckConstraint("CK_Products_Price_NonNegative", "[Price] >= 0"));
            e.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique();
            // Serves the product list: seek by tenant, then walk the index in the exact ORDER BY of the keyset query. No sort step.
            e.HasIndex(x => new { x.TenantId, x.IsActive, x.CreatedAt, x.Id }).IsDescending(false, false, true, true);
            e.HasIndex(x => new { x.TenantId, x.CategoryId, x.CreatedAt, x.Id }).IsDescending(false, false, true, true);
            e.HasIndex(x => new { x.TenantId, x.Name });               // prefix search: Name LIKE 'abc%'
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Order>(e =>
        {
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Property(x => x.PaymentReference).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAt, x.Id }).IsDescending(false, false, true, true);   // "my orders", newest first
            e.HasIndex(x => new { x.TenantId, x.CreatedAt, x.Id }).IsDescending(false, true, true);   // admin "all orders" list: found missing by sql/02 (961 -> 6 page reads)
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrderItem>(e =>
        {
            e.Property(x => x.ProductName).HasMaxLength(200);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.ProductId);                              // FK columns are NOT indexed automatically in SQL Server - the report joins on this
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(500);
            // FILTERED INDEX: only unprocessed rows are indexed, so it stays tiny however large the table grows.
            e.HasIndex(x => x.CreatedAt).HasFilter("[ProcessedAt] IS NULL");
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Key });                    // composite PK = the uniqueness that makes the race safe
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.RequestHash).HasMaxLength(64);
        });

        b.Entity<ProcessedWebhookEvent>(e => { e.HasKey(x => x.EventId); e.Property(x => x.EventId).HasMaxLength(100); });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(60);
            e.Property(x => x.EntityType).HasMaxLength(60);
            e.Property(x => x.EntityId).HasMaxLength(60);
            e.Property(x => x.Details).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });
    }

    private void TenantFilter<T>(ModelBuilder b) where T : class, ITenantOwned =>
        b.Entity<T>().HasQueryFilter(e => e.TenantId == currentUser.TenantId);
}
