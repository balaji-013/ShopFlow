using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ShopFlow.Application.Abstractions;

namespace ShopFlow.Infrastructure.Persistence;

/// <summary>Lets `dotnet ef migrations add` build a context without running the whole web host.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private class NoUser : ICurrentUser { public bool IsAuthenticated => false; public Guid UserId => Guid.Empty; public Guid TenantId => Guid.Empty; public bool IsAdmin => false; }

    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                 ?? "Server=(localdb)\\MSSQLLocalDB;Database=ShopFlow;Trusted_Connection=True;TrustServerCertificate=True";
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        return new AppDbContext(opts, new NoUser());
    }
}
