using ShopFlow.Domain.Common;

namespace ShopFlow.Domain.Entities;

public enum Role { Customer = 0, Admin = 1 }

public class Tenant : Entity
{
    public string Code { get; private set; } = default!;   // e.g. "acme" - unique
    public string Name { get; private set; } = default!;

    private Tenant() { }
    public Tenant(string code, string name) { Code = code.ToLowerInvariant(); Name = name; }
}

public class User : Entity
{
    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = default!;
    public string PasswordHash { get; private set; } = default!;   // never the password, never reversible encryption
    public string FullName { get; private set; } = default!;
    public Role Role { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    private User() { }
    public User(Guid tenantId, string email, string fullName, string passwordHash, Role role)
    {
        TenantId = tenantId; Email = email.Trim().ToLowerInvariant(); FullName = fullName;
        PasswordHash = passwordHash; Role = role;
    }
    public void SetPasswordHash(string hash) => PasswordHash = hash;
}

public class Category : Entity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    private Category() { }
    public Category(Guid tenantId, string name) { TenantId = tenantId; Name = name; }
}

public class Product : Entity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CategoryId { get; private set; }
    public Category? Category { get; private set; }
    public string Sku { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = "";
    public decimal Price { get; private set; }
    public int Stock { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>SQL Server rowversion: optimistic concurrency token. Two admins editing the same product -> second save fails with 409.</summary>
    public byte[] RowVersion { get; private set; } = [];

    private Product() { }
    public Product(Guid tenantId, Guid categoryId, string sku, string name, string description, decimal price, int stock)
    {
        TenantId = tenantId; CategoryId = categoryId; Sku = sku; Name = name; Description = description;
        SetPrice(price); SetStock(stock);
    }

    public void Update(string name, string description, decimal price, Guid categoryId)
    {
        Name = name; Description = description; CategoryId = categoryId; SetPrice(price);
    }
    public void SetPrice(decimal price)
    {
        if (price < 0) throw new DomainException("Price cannot be negative.", "invalid_price");
        Price = price;
    }
    public void SetStock(int stock)
    {
        if (stock < 0) throw new DomainException("Stock cannot be negative.", "invalid_stock");
        Stock = stock;
    }
    public void Deactivate() => IsActive = false;
}
