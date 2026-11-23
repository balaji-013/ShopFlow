using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopFlow.Infrastructure.Messaging;
using ShopFlow.Infrastructure.Persistence;

namespace ShopFlow.IntegrationTests;

[CollectionDefinition("api")] public class ApiCollection : ICollectionFixture<ApiFactory>;

// ───────────────────────── AUTH & SECURITY ─────────────────────────
[Collection("api")]
public class AuthTests(ApiFactory f)
{
    [Fact]
    public async Task Wrong_password_and_unknown_user_give_the_same_401()
    {
        var http = f.CreateClient();
        var wrong = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "demo@acme.test", password = "nope-nope-nope" });
        var unknown = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "ghost@acme.test", password = "nope-nope-nope" });
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadAsStringAsync()).Should().Contain("Invalid credentials");
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("Invalid credentials");   // no user enumeration
    }

    [Fact]
    public async Task Refresh_token_lives_in_an_httpOnly_cookie_not_in_the_body()
    {
        var res = await f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = "demo@acme.test", password = "Customer#12345" });
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("sf_refresh="));
        cookie.ToLower().Should().Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/api/v1/auth");
        (await res.Content.ReadAsStringAsync()).Should().NotContain("refreshToken");
    }

    [Fact]
    public async Task Refresh_rotates_and_REUSE_of_an_old_token_revokes_the_whole_family()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var t1 = s.RefreshCookie;

        HttpRequestMessage Refresh(string t) { var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh"); r.Headers.Add("Cookie", $"sf_refresh={t}"); return r; }

        var r2 = await s.Http.SendAsync(Refresh(t1));
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        var t2 = TestExtensions.ExtractRefresh(r2);
        t2.Should().NotBe(t1);                                                    // rotated

        (await s.Http.SendAsync(Refresh(t1))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);   // replay of the used token = theft signal
        (await s.Http.SendAsync(Refresh(t2))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);   // ...so the legit newer token is dead too
    }

    [Fact]
    public async Task Two_simultaneous_refreshes_with_one_token_only_one_wins()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        HttpRequestMessage Refresh() { var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh"); r.Headers.Add("Cookie", $"sf_refresh={s.RefreshCookie}"); return r; }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => s.Http.SendAsync(Refresh())));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Protected_endpoints_need_a_token_and_admin_endpoints_need_the_admin_role()
    {
        (await f.CreateClient().GetAsync("/api/v1/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var customer = await f.LoginAsync("demo@acme.test", "Customer#12345");
        (await customer.Http.GetAsync("/api/v1/reports/top-products")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = await f.LoginAsync("admin@acme.test", "Admin#12345");
        (await admin.Http.GetAsync("/api/v1/reports/top-products")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Registration_validates_input_and_rejects_duplicate_email()
    {
        var http = f.CreateClient();
        var bad = await http.PostAsJsonAsync("/api/v1/auth/register", new { email = "not-an-email", password = "short", fullName = "", tenantCode = "acme" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadFromJsonAsync<Problem>())!.Code.Should().Be("validation_failed");

        var dup = await http.PostAsJsonAsync("/api/v1/auth/register", new { email = "demo@acme.test", password = "long-enough-password", fullName = "X", tenantCode = "acme" });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Security_headers_and_correlation_id_are_present()
    {
        var res = await f.CreateClient().GetAsync("/health/live");
        res.Headers.Should().ContainKey("X-Content-Type-Options");
        res.Headers.Should().ContainKey("X-Correlation-Id");
    }
}

// ───────────────────────── MULTI-TENANCY ─────────────────────────
[Collection("api")]
public class TenantIsolationTests(ApiFactory f)
{
    [Fact]
    public async Task Each_tenant_only_sees_its_own_catalogue()
    {
        var acme = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var globex = await f.LoginAsync("demo@globex.test", "Customer#12345");
        (await acme.Http.ListProductsAsync()).Should().OnlyContain(p => p.Sku.StartsWith("AC-"));
        (await globex.Http.ListProductsAsync()).Should().OnlyContain(p => p.Sku.StartsWith("GL-"));
    }

    [Fact]
    public async Task Another_tenants_resource_is_404_not_403()
    {
        var acme = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var globex = await f.LoginAsync("demo@globex.test", "Customer#12345");
        var product = (await acme.Http.ListProductsAsync())[10];

        (await globex.Http.GetAsync($"/api/v1/products/{product.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var order = await acme.Http.PostOrderAsync(product.Id, 1);
        var id = (await order.Content.ReadFromJsonAsync<OrderBody>())!.Id;
        (await globex.Http.GetAsync($"/api/v1/orders/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_customer_cannot_read_another_customers_order_in_the_same_tenant()
    {
        var alice = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var bob = await f.CreateClient().PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"bob{Guid.NewGuid():N}@acme.test", password = "long-enough-password", fullName = "Bob", tenantCode = "acme" });
        var bobToken = (await bob.Content.ReadFromJsonAsync<SessionBody>())!.AccessToken;

        var p = (await alice.Http.ListProductsAsync())[11];
        var id = (await (await alice.Http.PostOrderAsync(p.Id, 1)).Content.ReadFromJsonAsync<OrderBody>())!.Id;

        var bobHttp = f.CreateClient();
        bobHttp.DefaultRequestHeaders.Authorization = new("Bearer", bobToken);
        (await bobHttp.GetAsync($"/api/v1/orders/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

// ───────────────────────── CATALOGUE ─────────────────────────
[Collection("api")]
public class CatalogTests(ApiFactory f)
{
    [Fact]
    public async Task Cursor_pagination_visits_every_product_exactly_once()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var seen = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await s.Http.GetFromJsonAsync<Page<ProductBody>>($"/api/v1/products?limit=7{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
            seen.AddRange(page!.Items.Select(p => p.Id)); cursor = page.NextCursor;
        } while (cursor is not null);
        seen.Should().HaveCount(60).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Garbage_cursor_is_a_400_and_page_size_is_capped()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        (await s.Http.GetAsync("/api/v1/products?cursor=%25%25%25")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await s.Http.ListProductsAsync(limit: 100000)).Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Search_is_prefix_based_and_survives_LIKE_wildcards_and_SQL_injection_attempts()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var hits = await s.Http.GetFromJsonAsync<Page<ProductBody>>("/api/v1/products?search=Laptop");
        hits!.Items.Should().NotBeEmpty().And.OnlyContain(p => p.Name.StartsWith("Laptop"));

        (await s.Http.GetFromJsonAsync<Page<ProductBody>>("/api/v1/products?search=%25"))!.Items.Should().BeEmpty();   // '%' is escaped, not a wildcard
        var inj = await s.Http.GetAsync("/api/v1/products?search=" + Uri.EscapeDataString("'; DROP TABLE Products;--"));
        inj.StatusCode.Should().Be(HttpStatusCode.OK);                                                                // parameterised => just a string
        (await s.Http.ListProductsAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Optimistic_concurrency_second_writer_gets_409()
    {
        var admin = await f.LoginAsync("admin@acme.test", "Admin#12345");
        var p = (await admin.Http.ListProductsAsync())[20];
        var cat = (await admin.Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/products/{p.Id}")).GetProperty("categoryId").GetGuid();

        var first = await admin.Http.PutAsJsonAsync($"/api/v1/products/{p.Id}", new { name = "Edited by A", description = "", price = 11.11m, categoryId = cat, version = p.Version });
        var second = await admin.Http.PutAsJsonAsync($"/api/v1/products/{p.Id}", new { name = "Edited by B", description = "", price = 22.22m, categoryId = cat, version = p.Version });   // stale version

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<Problem>())!.Code.Should().Be("concurrency_conflict");
    }

    [Fact]
    public async Task Customers_cannot_create_products()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var res = await s.Http.PostAsJsonAsync("/api/v1/products", new { sku = "X", name = "X", description = "", price = 1, stock = 1, categoryId = Guid.NewGuid() });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

// ───────────────────────── ORDERS: concurrency, idempotency, outbox ─────────────────────────
[Collection("api")]
public class OrderTests(ApiFactory f)
{
    [Fact]
    public async Task Forty_buyers_race_for_ten_items_exactly_ten_succeed_and_stock_never_goes_negative()
    {
        var admin = await f.LoginAsync("admin@acme.test", "Admin#12345");
        var buyer = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var product = (await admin.Http.ListProductsAsync())[30];
        await admin.Http.SetStockAsync(product, 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => buyer.Http.PostOrderAsync(product.Id, 1)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(10);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(30);
        responses.Should().NotContain(r => (int)r.StatusCode >= 500);
        (await admin.Http.GetFromJsonAsync<ProductBody>($"/api/v1/products/{product.Id}"))!.Stock.Should().Be(0);
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_creates_one_order()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var p = (await s.Http.ListProductsAsync())[31];
        var key = Guid.NewGuid().ToString();

        // 5 truly concurrent duplicates (double-click / retry storm)
        var rs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => s.Http.PostOrderAsync(p.Id, 2, key)));
        rs.Should().OnlyContain(r => r.IsSuccessStatusCode);
        var ids = (await Task.WhenAll(rs.Select(r => r.Content.ReadFromJsonAsync<OrderBody>()))).Select(o => o!.Id).Distinct();
        ids.Should().ContainSingle();

        var count = await f.WithDbAsync(db => db.Orders.IgnoreQueryFilters().CountAsync(o => o.UserId == s.UserId && o.Items.Any(i => i.ProductId == p.Id)));
        count.Should().Be(1);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_422_and_missing_key_is_422()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var p = (await s.Http.ListProductsAsync())[32];
        var key = Guid.NewGuid().ToString();
        (await s.Http.PostOrderAsync(p.Id, 1, key)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await s.Http.PostOrderAsync(p.Id, 2, key)).StatusCode.Should().Be((HttpStatusCode)422);

        var noKey = await s.Http.PostAsJsonAsync("/api/v1/orders", new { items = new[] { new { productId = p.Id, quantity = 1 } } });
        noKey.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Orders_touching_the_same_products_in_opposite_order_never_deadlock()
    {
        var admin = await f.LoginAsync("admin@acme.test", "Admin#12345");
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var ps = await admin.Http.ListProductsAsync();
        var (a, b) = (ps[33], ps[34]);
        await admin.Http.SetStockAsync(a, 1000); await admin.Http.SetStockAsync(b, 1000);

        var rs = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? s.Http.PostOrderAsync(a.Id, 1, second: b.Id) : s.Http.PostOrderAsync(b.Id, 1, second: a.Id)));
        rs.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);        // would show 500s (deadlock victims) without a stable lock order
    }

    [Fact]
    public async Task Cancelling_returns_stock_and_a_paid_order_cannot_be_cancelled()
    {
        var admin = await f.LoginAsync("admin@acme.test", "Admin#12345");
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var p = (await admin.Http.ListProductsAsync())[35];
        await admin.Http.SetStockAsync(p, 5);

        var o = (await (await s.Http.PostOrderAsync(p.Id, 3)).Content.ReadFromJsonAsync<OrderBody>())!;
        (await admin.Http.GetFromJsonAsync<ProductBody>($"/api/v1/products/{p.Id}"))!.Stock.Should().Be(2);

        (await s.Http.PostAsync($"/api/v1/orders/{o.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Http.GetFromJsonAsync<ProductBody>($"/api/v1/products/{p.Id}"))!.Stock.Should().Be(5);

        var paid = (await (await s.Http.PostOrderAsync(p.Id, 1)).Content.ReadFromJsonAsync<OrderBody>())!;
        (await s.Http.PostAsync($"/api/v1/dev/orders/{paid.Id}/simulate-payment", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await s.Http.PostAsync($"/api/v1/orders/{paid.Id}/cancel", null)).StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Outbox_message_is_written_with_the_order_and_dispatched_to_the_user()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var p = (await s.Http.ListProductsAsync())[36];
        var o = (await (await s.Http.PostOrderAsync(p.Id, 1)).Content.ReadFromJsonAsync<OrderBody>())!;

        var before = f.Notifier.Sent.Count;
        var processed = await f.WithDbAsync(async db =>
        {
            var d = new OutboxDispatcher(db, f.Notifier, f.Services.GetRequiredService<ILogger<OutboxDispatcher>>());
            return await d.ProcessBatchAsync(100, default);
        });
        processed.Should().BeGreaterThan(0);
        f.Notifier.Sent.Skip(before).Should().Contain(n => n.UserId == s.UserId && n.Event == "order.updated");
        (await f.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.ProcessedAt == null))).Should().Be(0);
    }
}

// ───────────────────────── PAYMENT WEBHOOK ─────────────────────────
[Collection("api")]
public class WebhookTests(ApiFactory f)
{
    [Fact]
    public async Task Unsigned_or_forged_webhooks_are_rejected()
    {
        var http = f.CreateClient();
        var body = new StringContent("{\"id\":\"evt_x\",\"type\":\"payment.succeeded\",\"data\":{\"orderId\":\"" + Guid.NewGuid() + "\",\"reference\":\"r\"}}");
        (await http.PostAsync("/api/v1/webhooks/payments", body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments") { Content = body };
        forged.Headers.Add("X-Signature", "t=1,v1=deadbeef");
        (await http.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_marks_paid_once_and_emits_one_message()
    {
        var s = await f.LoginAsync("demo@acme.test", "Customer#12345");
        var p = (await s.Http.ListProductsAsync())[37];
        var o = (await (await s.Http.PostOrderAsync(p.Id, 1)).Content.ReadFromJsonAsync<OrderBody>())!;

        (await s.Http.PostAsync($"/api/v1/dev/orders/{o.Id}/simulate-payment?duplicate=true", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await s.Http.GetFromJsonAsync<OrderBody>($"/api/v1/orders/{o.Id}"))!.Status.Should().Be("Paid");
        var paidMsgs = await f.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Type == "OrderPaid" && m.Payload.Contains(o.Id.ToString())));
        paidMsgs.Should().Be(1);
    }
}

// ───────────────────────── REPORTING (raw SQL + window function) ─────────────────────────
[Collection("api")]
public class ReportTests(ApiFactory f)
{
    [Fact]
    public async Task Top_products_report_ranks_within_category_and_never_leaks_another_tenant()
    {
        var acmeAdmin = await f.LoginAsync("admin@acme.test", "Admin#12345");
        var globexBuyer = await f.LoginAsync("demo@globex.test", "Customer#12345");
        var gp = (await globexBuyer.Http.ListProductsAsync())[0];
        var go = (await (await globexBuyer.Http.PostOrderAsync(gp.Id, 1)).Content.ReadFromJsonAsync<OrderBody>())!;
        await globexBuyer.Http.PostAsync($"/api/v1/dev/orders/{go.Id}/simulate-payment", null);   // Globex has a paid order

        var rows = await acmeAdmin.Http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/reports/top-products?top=2");
        foreach (var r in rows.EnumerateArray())
        {
            r.GetProperty("rnk").GetInt64().Should().BeLessThanOrEqualTo(2);
            r.GetProperty("productId").GetGuid().Should().NotBe(gp.Id);        // Globex's product never appears in Acme's report
        }
    }
}

