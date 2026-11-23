using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Application.Abstractions;
using ShopFlow.Infrastructure.Persistence;

namespace ShopFlow.IntegrationTests;

/// <summary>
/// Boots the REAL app in-memory (same DI, middleware, EF mappings) against a REAL SQL Server database created fresh for the test run.
/// Why not mock the DB / use SQLite? The bugs that matter here (row locks, rowversion, unique-index races, window functions,
/// filtered indexes) only exist in the real engine. "Mocks hide the bugs that actually reach production."
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _db = $"ShopFlow_Test_{Guid.NewGuid():N}";
    public static string ServerCs => Environment.GetEnvironmentVariable("TEST_SQL_SERVER")
        ?? "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";
    public RecordingNotifier Notifier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", $"{ServerCs};Database={_db}");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Database:Seed", "true");
        builder.UseSetting("Outbox:Enabled", "false");             // tests drive the dispatcher explicitly => deterministic
        builder.UseSetting("RateLimiting:AuthPerMinute", "100000");
        builder.UseSetting("RateLimiting:GlobalPerMinute", "1000000");
        builder.ConfigureServices(s => { s.RemoveAll<IRealtimeNotifier>(); s.AddSingleton<IRealtimeNotifier>(Notifier); });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var c = new SqlConnection(ServerCs); await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]; END";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Direct DB access for assertions. No logged-in user => tenant filter would hide everything, so tests use IgnoreQueryFilters().</summary>
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

public class RecordingNotifier : IRealtimeNotifier
{
    public List<(Guid UserId, string Event, object Payload)> Sent { get; } = [];
    public Task NotifyUserAsync(Guid userId, string eventName, object payload, CancellationToken ct)
    { lock (Sent) Sent.Add((userId, eventName, payload)); return Task.CompletedTask; }
}

public record Session(HttpClient Http, string RefreshCookie, Guid UserId);

public static class TestExtensions
{
    public static async Task<Session> LoginAsync(this ApiFactory f, string email, string password)
    {
        var http = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });   // we manage the refresh cookie by hand
        var res = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<SessionBody>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return new Session(http, ExtractRefresh(res), body.User.Id);
    }

    public static string ExtractRefresh(HttpResponseMessage res) =>
        res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("sf_refresh=")).Split(';')[0]["sf_refresh=".Length..];

    public static Task<HttpResponseMessage> PostOrderAsync(this HttpClient http, Guid productId, int qty, string? key = null, Guid? second = null)
    {
        var items = new List<object> { new { productId, quantity = qty } };
        if (second is { } s) items.Add(new { productId = s, quantity = qty });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(new { items }) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return http.SendAsync(req);
    }

    public static async Task<ProductBody[]> ListProductsAsync(this HttpClient http, int limit = 100)
    {
        var page = await http.GetFromJsonAsync<Page<ProductBody>>($"/api/v1/products?limit={limit}");
        return page!.Items;
    }

    /// <summary>Sets stock to an exact value via the admin API (relative adjustment).</summary>
    public static async Task SetStockAsync(this HttpClient admin, ProductBody p, int target)
    {
        var cur = (await admin.GetFromJsonAsync<ProductBody>($"/api/v1/products/{p.Id}"))!.Stock;
        var res = await admin.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock-adjustments", new { delta = target - cur });
        res.EnsureSuccessStatusCode();
    }
}

public record UserBody(Guid Id, string Email, string Role);
public record SessionBody(string AccessToken, int ExpiresIn, UserBody User);
public record ProductBody(Guid Id, string Sku, string Name, decimal Price, int Stock, string Version);
public record Page<T>(T[] Items, string? NextCursor);
public record OrderBody(Guid Id, string Status, decimal Total);
public record Problem(int Status, string Code);

