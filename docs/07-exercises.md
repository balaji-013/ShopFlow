# Exercises - learn by breaking it, then by extending it

> *"Nobody is hired for finishing tutorials."* - the kit. Reading code teaches little; **changing one line and watching a specific test fail** teaches a lot.

**How to do a "break it" exercise:** make a copy of the file (or use `git stash`), apply the mutation, run the named test, read the failure, then **restore** the file.
Exercises marked **[verified]** were actually run while this repo was built; the observed result is quoted. The others say what to expect and why - confirm it yourself and note any difference.

Run a single backend test: `dotnet test backend/tests/ShopFlow.IntegrationTests --filter "FullyQualifiedName~Forty_buyers"` · frontend: `npm test` (in `frontend/`).

---
## Part 1 - Break it on purpose (7.1 - 7.8)

### 7.1 The oversell **[verified]**
**Mutate** `OrderService.CreateOrderTransactionAsync`: replace the atomic `ExecuteUpdateAsync(... WHERE Stock >= q ...)` with *read stock -> check in C# -> write `Stock = read - q`* (add `await Task.Delay(5)` between to widen the window).
**Run** `Forty_buyers_race_for_ten_items...`.
**Observed:** `Expected responses.Count(r => r.StatusCode == Created) to be 10, but found 40` - every one of 40 buyers got an order for a product with 10 units.
**Learn:** a check followed by a write is a race. Make the *database* arbitrate, in a single statement. Then read `sql/03` scenario 1-2 and explain why `READ COMMITTED` alone didn't save you.

### 7.2 Remove the lock ordering **[verified - surprising result]**
**Mutate:** delete `.OrderBy(l => l.ProductId)` in `PlaceAsync` and add a `Task.Delay(20)` after each stock update so transactions interleave.
**Run** `Orders_touching_the_same_products_in_opposite_order_never_deadlock`.
**Observed:** the test **still passed** - but took **72 s instead of 8 s** (both numbers include creating the test database). `EnableRetryOnFailure` classifies a deadlock victim (SQL error 1205) as *transient* and silently re-runs the whole transaction; the retries (with back-off) ate the time.
**Learn:** a green test can hide contention. Retries are a *safety net*, not a design. To see the truth: log the retries (set `Microsoft.EntityFrameworkCore.Database.Command` to Debug or watch `sys.dm_exec_requests`/the `system_health` deadlock graphs), or temporarily disable `EnableRetryOnFailure` and re-run - now the victims surface as 500s. Then restore the ordering and watch the time drop. This is what "consistent lock order" buys: no cycle can form.

### 7.3 Forget the idempotency record **[verified]**
**Mutate:** remove the `db.IdempotencyRecords.Add(...)` call.
**Run** `Retrying_with_the_same_idempotency_key_creates_one_order`.
**Observed:** `Expected ids to contain a single item, but found {5 different order ids}` - five concurrent retries created five orders.
**Learn:** idempotency needs a **durable, unique** record written in the *same transaction* as the effect. Bonus: first remove only `TryReplayAsync` (the pre-check) and notice the test still passes - why? (The composite primary key makes the loser fail, and the `catch (DbUpdateException)` path returns the winner's response. The pre-check is an optimisation; the **constraint is the guarantee**.)

### 7.4 Replace the manual "busy" flag with `exhaustMap`
**Task:** in `cart.page.ts`, `checkout()` guards double-clicks with `if (this.busy()) return;`. Re-implement with a `Subject<void>` + `exhaustMap(() => api.placeOrder(...))`, which ignores clicks while a request is in flight.
**Prove:** write a Vitest test that fires `checkout()` three times quickly and asserts `HttpTestingController` saw one request. Then explain why the Idempotency-Key is *still* needed (page reload, retry from another tab, flaky network retries).

### 7.5 Delete the tenant filter
**Mutate:** remove `TenantFilter<Product>(b);` in `AppDbContext`.
**Expect:** `Each_tenant_only_sees_its_own_catalogue` fails (Acme sees `GL-` SKUs); `Another_tenants_resource_is_404_not_403` fails (Globex reads Acme's product). Note that **no controller code changed** - that is the point of enforcing isolation centrally.
**Extend:** now also remove the filter from `Order` and see which tests catch *that* (and which behaviours aren't covered - write the missing test).

### 7.6 Break refresh-token theft detection
**Mutate:** in `AuthService.RefreshAsync`, delete the family-revocation `ExecuteUpdateAsync` inside the `UsedAt != null` branch (keep the 401).
**Expect:** `Refresh_rotates_and_REUSE_of_an_old_token_revokes_the_whole_family` fails at its last assertion - the legitimate newer token still works after a replay, so a thief who replays an old token is only *partially* stopped.
**Think:** what happens to the real user in each design? (Revoking the family logs them out too: the cost of safety.)

### 7.7 Trust the webhook
**Mutate:** in `PaymentWebhookService.HandleAsync` remove the signature check.
**Expect:** `Unsigned_or_forged_webhooks_are_rejected` fails (200 instead of 401). Then, by hand with `curl`, mark someone's order *Paid* with a forged body - feel why this is the highest-risk endpoint in the system.

### 7.8 Break single-flight token refresh **[verified]**
**Mutate** `AuthStore.refresh()`: delete the `shareReplay({ bufferSize: 1, refCount: false })` line.
**Run** `npm test`.
**Observed:** `Expected one matching request for criteria "Match URL: /api/v1/auth/refresh", found 3 requests.` Against the real API, 2 of those 3 refreshes would be rejected as token reuse and the user would be logged out at random.
**Learn:** share one in-flight Observable among concurrent subscribers; make the same thing true for any single-use credential.

---
## Part 2 - Observe and tune (7.9 - 7.10)

### 7.9 Watch the rate limiter
Start the API. Run 15 bad logins in a minute:
```powershell
1..15 | % { curl.exe -s -o NUL -w "$_ -> %{http_code}`n" -H "Content-Type: application/json" -d '{\"email\":\"x@x.test\",\"password\":\"nope\"}' http://localhost:5080/api/v1/auth/login }
```
**Expect:** the first 10 return `401`, then `429` with a `Retry-After` header (config: `RateLimiting:AuthPerMinute`). Questions: why partition by IP for `/auth` but by user id for the global limiter? What breaks behind a proxy if forwarded headers are mis-trusted (everyone shares the proxy's IP -> one user's burst blocks all; or attackers spoof the header to evade)?

### 7.10 Provider down
Set `Payments:BaseUrl` to `http://localhost:9` (nothing listens), start the API, place an order and click *Pay*.
**Expect:** a `503` problem+json with `Retry-After`, the order still **Pending**, and (after repeated failures) the circuit breaker opens so later calls fail instantly. Read the resilience handler options and change retry/timeout values; log the attempts. Write an integration test that swaps `IPaymentGateway` for a throwing fake and asserts the 503 + Pending state.

---
## Part 3 - Extend it (7.11 - 7.20)

| # | Build | Hints / acceptance criteria |
|---|---|---|
| **7.11** | **Redis cache + SignalR backplane** | `docker compose` add `redis`; replace `CatalogCache` internals with `HybridCache`/`IDistributedCache`; invalidate across instances (pub/sub or version key). Run **2 API instances** behind nginx and prove a stock change on instance A refreshes instance B's pages and pushes SignalR to a user connected to B. Add a cache-stampede test (100 parallel misses -> 1 DB query). |
| **7.12** | **Row-Level Security in SQL Server** | Set `SESSION_CONTEXT('TenantId')` from a `DbConnectionInterceptor`; add the security policy sketched in `04-sql-concepts.md` §7. Acceptance: remove the EF global filter -> tenant tests still pass; a raw SQL report without a tenant predicate returns only the caller's rows. |
| **7.13** | **Full-text product search** | Create a full-text catalog on `Products(Name, Description)`; add `CONTAINS`-based search (keep `LIKE 'x%'` as comparison). Benchmark both on 100k products with `STATISTICS IO/TIME` and write up the numbers (the kit asks for exactly this comparison). |
| **7.14** | **OAuth 2.0 / OIDC login** | Add "Sign in with Google/Microsoft" using Authorization Code + **PKCE**; map the external identity to a `User`; keep your refresh-cookie session afterwards. Explain `state`, `nonce`, redirect-URI allow-listing, and why implicit flow is dead. |
| **7.15** | **Real payments (Stripe test mode)** | Create a PaymentIntent in `HttpPaymentGateway`; verify Stripe's `Stripe-Signature`; use Stripe CLI to deliver events (including duplicates) and prove idempotency; handle `payment_intent.payment_failed`. |
| **7.16** | **Playwright E2E** | `docker compose up`; test: register -> add to cart -> place order -> pay -> status becomes Paid **without reload** (SignalR). Add a flow where two browsers race for the last item. Run it in CI. |
| **7.17** | **Product images: large uploads** | Admin uploads an image (up to 10 MB, then try 2 GB): stream to Azurite/MinIO (S3-compatible) with **pre-signed upload URLs** so the file never passes through the API; validate type by content (magic bytes) not extension; generate thumbnails in a background job; serve via CDN with long cache headers. |
| **7.18** | **Tailwind** | Port `styles.scss` to Tailwind utilities with the same design tokens and dark mode; compare CSS size and note what you gained and lost. |
| **7.19** | **Angular SSR + hydration for a public catalogue** | `ng add @angular/ssr`; prerender `/` and product pages; keep the logged-in area client-rendered. Measure LCP before/after with Lighthouse; handle `localStorage`/`window` safely on the server. |
| **7.20** | **GraphQL read API** | HotChocolate over EF for products/categories/orders with **DataLoader** to avoid N+1 (reproduce the N+1 first with a naive resolver, show the query count, then fix it). Add depth/complexity limits and explain why persisted queries matter. |

### More ideas once those are done
* **OpenTelemetry** traces + metrics to a local collector (Jaeger/Grafana); add a dashboard for p95 latency and queue depth.
* **k6 load test** for `POST /orders` on a single hot SKU; chart throughput vs lock waits; then implement sharded stock counters.
* **Soft deletes + audit trail** with EF `SaveChanges` interceptors; temporal tables for history.
* **Background jobs UI**: dead-letter outbox messages after 5 attempts (already tracked via `Attempts`) and add an admin screen to retry them.
* **Feature flags** (`Microsoft.FeatureManagement`) and a canary deployment.
* **Security review**: run OWASP ZAP against the compose stack and triage the findings; add a CSP to nginx for the SPA.

## How to know you've learned it
You can (1) **draw** the order-placement and refresh-token flows from memory, (2) **predict** which test fails when a given line is removed, (3) **explain the trade-off** behind each decision in `01-architecture.md`'s decision log, and (4) **extend** it without breaking the suite.