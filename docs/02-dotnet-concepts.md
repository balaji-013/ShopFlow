# .NET concepts a senior must own - each tied to a file you can open

Format: **concept** -> *what a senior can explain* -> `where it lives`.

## 1. Dependency Injection

* **Lifetimes.** *Singleton* = one per process, *Scoped* = one per HTTP request, *Transient* = new every time.
  `AppDbContext` is **scoped** (one unit of work per request, not thread-safe). `CatalogCache`, `TokenService`, `Pbkdf2PasswordHasher` are **singletons** (stateless or own shared state).
  `DependencyInjection.cs` in `Application` and `Infrastructure`.
* **Captive dependency** - a singleton must never hold a scoped service (it would keep one `DbContext` alive forever, shared across threads).
  `OutboxWorker` is a singleton `BackgroundService`, so it takes `IServiceScopeFactory` and creates **a scope per batch**. `Messaging/Outbox.cs`.
* **Composition root.** All wiring happens in `Program.cs` + two `AddX()` extension methods. No `new` of services anywhere else; no service locator.
* **Interfaces at boundaries only** where you need to swap/fake: `IPaymentGateway`, `IRealtimeNotifier`, `IWebhookSignatureVerifier`, `TimeProvider`. Not an interface for every class.
* **Options pattern.** `JwtOptions`/`WebhookOptions` bound from configuration, injected as `IOptions<T>`. (`IOptionsSnapshot` = per-request reload, `IOptionsMonitor` = live reload.)

## 2. async / await

* `await` releases the thread while I/O is pending; the thread-pool serves other requests. **Async is about scalability (threads), not speed of one request.**
* **Never block on async** (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`): under load it starves the thread pool. Every method in the repo is async end-to-end.
* **Pass `CancellationToken`** from the controller down to EF (`ct`). If the client disconnects (`RequestAborted`), the query is cancelled and the DB stops working.
  The exception handler maps it to 499 and does not log it as an error. `Http.cs`.
* `async void` is only for event handlers (exceptions crash the process). `Task.WhenAll` for independent work (the integration tests fire 40 requests this way).
* Library code would use `ConfigureAwait(false)`; ASP.NET Core has no synchronisation context so app code does not need it.
* `ValueTask` only when profiling shows allocation pressure on a hot, often-synchronous path.

## 3. EF Core (the part interviews dig into)

| Concept | Senior-level explanation | Where |
|---|---|---|
| **Change tracking** | `DbContext` snapshots entities it loads and diffs on `SaveChanges`. Read-only queries use `AsNoTracking()` (cheaper). | `ProductService.QueryAsync` |
| **Projection** | `.Select(p => new {...})` fetches only needed columns, no tracking, no entity graph. | `ProductService.GetAsync` |
| **Deferred execution** | `IQueryable` builds an expression tree; SQL runs at `ToListAsync/First/Any/Count`. Composing `Where` before executing keeps filtering in SQL; calling `.ToList()` early filters in memory (classic perf bug). | `ProductService.QueryAsync` composes filters conditionally |
| **N+1** | One query for N parents + N queries for children. Fix with `Include`, projection, or explicit join. `AsSplitQuery` avoids the cartesian explosion of one giant JOIN for collections. | `OrderService.ListAsync` |
| **Set-based writes** | `ExecuteUpdateAsync/ExecuteDeleteAsync` run one SQL statement without loading entities; the atomic stock decrement relies on it. | `OrderService`, `ProductService.AdjustStockAsync`, `AuthService.RefreshAsync` |
| **Optimistic concurrency** | `[Timestamp]/IsRowVersion()` column added to `WHERE` of UPDATE; 0 rows -> `DbUpdateConcurrencyException`. | `AppDbContext`, `ProductService.UpdateAsync` |
| **Transactions** | `SaveChanges` is already atomic. Use an explicit transaction when mixing multiple `SaveChanges`/`ExecuteUpdate`. | `OrderService.CreateOrderTransactionAsync` |
| **Execution strategy** | With `EnableRetryOnFailure`, user transactions **must** run inside `CreateExecutionStrategy().ExecuteAsync(...)`, and the lambda must be re-runnable -> `ChangeTracker.Clear()` at the start. | same |
| **Global query filters** | Automatic `WHERE TenantId = @current`. `IgnoreQueryFilters()` is an explicit, greppable opt-out (webhook is a system actor). **Raw SQL is not filtered.** | `AppDbContext.TenantFilter`, `PaymentWebhookService`, `ReportService` |
| **Raw SQL safely** | `SqlQuery<T>($"... {value}")` takes a `FormattableString` and sends **parameters**. `FromSqlRaw("..." + x)` is injection. | `ReportService`, `OutboxDispatcher` |
| **Migrations** | Code-first, reviewed in PRs, applied by a pipeline step (auto-migrate on start only for demos). | `Persistence/Migrations`, `Database:MigrateOnStartup` |
| **Index in the model** | `HasIndex(...).IsDescending(...)`, filtered index `HasFilter`, unique indexes as the *real* duplicate guard. | `AppDbContext` |
| **DbContext lifetime** | Short-lived, not thread-safe; never cache it in a singleton; `AddDbContextPool` is an optimisation, not a default. | `Infrastructure/DependencyInjection.cs` |
| **Value comparison pitfalls** | `Guid` has no `<` operator in C#; EF translates `id.CompareTo(other) < 0` into a SQL comparison (used for the keyset cursor). | `ProductService.QueryAsync` |

## 4. ASP.NET Core

* **Middleware vs filters.** Middleware wraps the whole pipeline (cross-cutting, HTTP-level: logging, CORS, auth, exception handling). Filters wrap MVC actions (model-level concerns). Order is declaration order. `Program.cs` documents each position.
* **`[ApiController]`** gives automatic 400 on model-binding/validation failure, binding-source inference and problem-details responses.
* **Global exception handling** with `IExceptionHandler` + `ProblemDetails`: one mapping table, stable `code`, no stack traces to clients, 5xx logged with the exception, 4xx logged as information. `Api/Infrastructure/Http.cs`.
* **Authentication vs authorization.** JwtBearer validates signature/issuer/audience/lifetime (30 s skew) and builds a `ClaimsPrincipal`. Policies decide access (`"Admin"`). **Ownership** (resource-level) is enforced in the query. Controllers/services never parse tokens; they use `ICurrentUser`.
* **Rate limiting** (built-in `Microsoft.AspNetCore.RateLimiting`): fixed window for `/auth` per IP; token bucket per user as a global limiter; `429` + `Retry-After`.
* **`HttpClientFactory` + resilience.** Typed client, pooled handlers (avoids socket exhaustion and stale DNS from `new HttpClient()` per call), `AddStandardResilienceHandler()` = rate limiter -> total timeout -> retry (exponential, jitter) -> circuit breaker -> attempt timeout. `PaymentGateways.cs`.
* **Idempotent retries:** a retry policy on a POST is only safe if the server dedupes - the gateway sends an `Idempotency-Key`.
* **Hosted services.** `BackgroundService` + `PeriodicTimer`; graceful shutdown through the stop token; catch-all so one bad batch does not kill the loop.
* **SignalR.** Hub groups/users, `IUserIdProvider` (we address by `sub`), token via query string for WebSockets only on `/hubs`, scale-out needs a backplane.
* **Health checks.** *Liveness* (process up) vs *readiness* (dependencies reachable) - orchestrators restart on liveness failure but only stop routing on readiness failure.
* **API versioning.** URL segment `/api/v1`, `api-supported-versions` header; adding `v2` = new controller class, old clients unaffected.
* **Configuration layering.** `appsettings.json` < `appsettings.{Env}.json` < env vars (`Jwt__SigningKey`) < command line; user-secrets locally. Fail fast on missing secrets (`Program.cs`).
* **Forwarded headers.** Behind a proxy the app must see the real client IP/scheme, but *trusting* `X-Forwarded-For` from anywhere lets attackers dodge rate limiting - hence the explicit `ForwardedHeaders:TrustAll` opt-in.

## 5. Modern C# features used on purpose

* **Primary constructors** (`class OrderService(IAppDbContext db, ...)`) - DI without boilerplate.
* **Records** for DTOs (immutable, value equality, `with`). **Classes** for entities (identity + mutation through intention-revealing methods, private setters).
* **Nullable reference types** + `TreatWarningsAsErrors` (`Directory.Build.props`): null bugs become compile errors.
* **Switch expressions / pattern matching** (`GlobalExceptionHandler`), **collection expressions** (`[]`), **raw string literals** (`ReportService` SQL).
* **Generics with constraints** (`TenantFilter<T> where T : class, ITenantOwned`).
* **`TimeProvider`** instead of `DateTime.UtcNow` so tests can control time (`FakeTimeProvider` in `WebhookSignatureTests`).
* **`await using`** for async-disposable transactions.

## 6. Domain modelling (DDD-lite, pragmatic)

* **Aggregate root.** `Order` owns its `OrderItem`s; the only way in is `Order.Create(...)`, which enforces "at least one item, quantity > 0, total derived".
* **State machine in one place** (`Order.Transitions`). The API cannot express an illegal transition: `Shipped` before `Paid` throws `DomainException` -> 422.
* **Rich vs anemic.** Entities have behaviour (`MarkPaid`, `Cancel`) and private setters; services orchestrate I/O and transactions.
* **Where a rule lives:** invariant of one entity -> entity; needs the DB/other aggregates -> application service; input shape -> validator; last line of defence -> DB constraint (`CHECK Stock >= 0`, unique index).

## 7. Testing .NET properly

* **Pyramid:** many fast unit tests (domain, HMAC, cursor, validators) -> fewer integration tests through the *real* pipeline and *real* SQL Server -> (E2E in `07-exercises.md`).
* **`WebApplicationFactory<Program>`** hosts the real app in-memory: real DI, middleware, auth, EF. `public partial class Program;` exposes the entry point. `ApiFactory` overrides config (`UseSetting`) and swaps only the edges (`IRealtimeNotifier`).
* **A fresh database per test run** (created by migrations - which also tests the migrations - and dropped afterwards).
* **Concurrency tests** use `Task.WhenAll` over real HTTP calls: *Forty buyers, ten items.* A race you cannot reproduce in a test is a race you have not fixed.
* **Fakes at the edge, not mocks of your own code.** `RecordingNotifier`, `FakePaymentGateway`, `FakeTimeProvider`.
* **Determinism:** the outbox worker is disabled in tests (`Outbox:Enabled=false`) and driven explicitly, so assertions never wait on timers.

## 8. Bugs this project actually hit while being built (read these - they are the real curriculum)

| Symptom | Root cause | Lesson | Fixed in |
|---|---|---|---|
| Admin got **403** even with a valid admin token | `new JwtSecurityToken(claims: ...)` does **not** apply the legacy outbound claim map, so the role was issued under the long URI `...ClaimTypes.Role`, while validation expected `"role"` | Know claim-type mapping (`MapInboundClaims`, `RoleClaimType`). Test authorization with a real token, not only unit tests | `SecurityServices.cs` |
| App crashed at startup: *Unable to resolve `IMemoryCache`* | A service used `IMemoryCache` but nothing called `AddMemoryCache()` | The DI container validates the graph at start in Development - read the *first* line of the exception | `Application/DependencyInjection.cs` |
| Request log said **500** for a handled **404** | Serilog request logging sat *inside* the exception handler, so it recorded the status before the handler rewrote it | Middleware order defines what each layer can see | `Program.cs` |
| `error CS7094: Cannot await in the filter expression of a catch clause` | `catch (X) when (await ...)` is illegal | Move the awaited check into the catch body | `PaymentWebhookService` |
| Cursor `"{}"` was accepted as a valid cursor | JSON deserialisation produced default values; no validation | **A unit test found it.** Treat all client-supplied opaque tokens as hostile | `Cursor.cs` |
| Refresh-reuse integration test failed with 200 | `HttpClient` from the factory had its own cookie jar and overrode my manual `Cookie` header | Test harnesses have behaviour of their own; `HandleCookies=false` | `Fixtures.cs` |
| Angular dev server crashed on start | PowerShell wrote `proxy.conf.json` with a **UTF-8 BOM** | Check encodings of machine-read config files | `proxy.conf.json` |
| Missing index on the admin "all orders" list | Found by running `sql/02` with 100k rows, not by reading code | Performance bugs hide at small data sizes; load-test your queries | migration `AddOrdersTenantCreatedIndex` |
| `catch (DbUpdateException)` after a failed concurrent insert still saw junk in the tracker | Failed `SaveChanges` leaves entities tracked | `ChangeTracker.Clear()` before reading again | `PaymentWebhookService` |