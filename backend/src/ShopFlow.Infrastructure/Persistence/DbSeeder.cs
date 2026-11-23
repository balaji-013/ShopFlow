using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShopFlow.Application.Abstractions;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Infrastructure.Persistence;

/// <summary>Demo data: 2 tenants (to prove isolation), an admin + a customer each, 5 categories, 60 products per tenant.</summary>
public class DbSeeder(AppDbContext db, IPasswordHasher hasher, ILogger<DbSeeder> log)
{
    public const string AdminPassword = "Admin#12345";
    public const string CustomerPassword = "Customer#12345";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.Tenants.AnyAsync(ct)) return;
        log.LogInformation("Seeding demo data...");

        var cats = new[] { "Laptops", "Phones", "Audio", "Accessories", "Gaming" };
        var rnd = new Random(42);
        foreach (var (code, name) in new[] { ("acme", "Acme Electronics"), ("globex", "Globex Gadgets") })
        {
            var tenant = new Tenant(code, name);
            db.Tenants.Add(tenant);
            db.Users.Add(new User(tenant.Id, $"admin@{code}.test", $"{name} Admin", hasher.Hash(AdminPassword), Role.Admin));
            db.Users.Add(new User(tenant.Id, $"demo@{code}.test", $"{name} Customer", hasher.Hash(CustomerPassword), Role.Customer));

            var categories = cats.Select(c => new Category(tenant.Id, c)).ToList();
            db.Categories.AddRange(categories);

            var nouns = new[] { "Pro", "Air", "Max", "Mini", "Ultra", "Lite", "Plus", "Edge" };
            for (var i = 1; i <= 60; i++)
            {
                var cat = categories[i % categories.Count];
                var productName = $"{cat.Name.TrimEnd('s')} {nouns[i % nouns.Length]} {100 + i}";
                db.Products.Add(new Product(tenant.Id, cat.Id, $"{code[..2].ToUpper()}-{i:D4}", productName,
                    $"{productName}: a great {cat.Name.ToLower().TrimEnd('s')} for everyday use.",
                    Math.Round((decimal)(rnd.NextDouble() * 900 + 20), 2), rnd.Next(5, 80)));
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
