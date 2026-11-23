# Concept map: the "Full-Stack Complete Resource Kit 2026" skills matrix, mapped onto ShopFlow

The kit's **Skills Matrix** (99 skills) and **Interview Prep** sheets are written around a Node/React stack. ShopFlow is the
**.NET 9 + Angular 21 + SQL Server** version of the same ground. This file answers, for every skill row: *"where do I see this in a real codebase?"*

**How to use it:** pick a row -> open the file -> run the test or the SQL that proves it -> try the matching "break it" exercise in
[`07-exercises.md`](07-exercises.md). Reading code is not learning; breaking it and watching the test fail is.

Level key (from the kit): **F** = Foundation (cannot skip) · **C** = Core (expected in every job description) · **A** = Advanced (differentiator).
Paths are relative to the repo root. `BE` = `backend/src/ShopFlow.`, `FE` = `frontend/src/app/`.

---

## Translation table: kit (Node/React) -> ShopFlow (.NET/Angular)

| Kit says | ShopFlow uses | Why it is the same idea |
|---|---|---|
| Express / Fastify / NestJS | **ASP.NET Core** controllers + middleware pipeline | Routing, middleware chain, DI container |
| Prisma / Drizzle / TypeORM | **EF Core 9** (+ raw SQL where LINQ is weak) | ORM, migrations, change tracking |
| PostgreSQL | **SQL Server** (LocalDB / container) | Relational engine; T-SQL window functions, rowversion, filtered indexes |
| BullMQ / Celery / SQS | **Transactional outbox + `BackgroundService`** | Work leaves the request; at-least-once delivery |
| Socket.IO | **SignalR** | WebSockets with fallbacks, user-targeted push |
| React hooks / Context / Zustand | **Angular signals + `computed` + `effect` + injectable stores** | Fine-grained reactive state |
| TanStack Query (server state) | **Typed `ApiService` + RxJS (`switchMap`, cursor paging)** | Server data is fetched, not globally stored |
| React Router | **Angular Router** (lazy `loadComponent`, functional guards) | Routing, params, guards, redirect target |
| React Hook Form + Zod | **Typed reactive forms** + server-side **FluentValidation** | Client validation = UX, server = truth |
| Vitest + React Testing Library | **Vitest (Angular 21 default)** + `TestBed` | Component/service tests |
| Jest/Vitest backend tests | **xUnit + FluentAssertions + `WebApplicationFactory`** | Unit + integration against a real DB |
| Zod / class-validator | **FluentValidation** | Declarative input validation |
| bcrypt / argon2 | **PBKDF2-HMAC-SHA512** via ASP.NET Identity hasher | Slow, salted, one-way |
| Redis cache | **`IMemoryCache` + change tokens** (Redis in the scaling doc) | Cache-aside + invalidation |
| Docker + GitHub Actions | `Dockerfile`s, `docker-compose.yml`, `.github/workflows/ci.yml` | Same |

---

## Web Fundamentals

| Skill | Lvl | Where to look | Prove it / observe it |
|---|---|---|---|
| How the web works (DNS, HTTP, request lifecycle) | F | [`01-architecture.md` § "What happens when you press Enter"](01-architecture.md) traces one request through browser -> nginx -> middleware -> controller -> SQL | Browser devtools Network tab while using the app |
| HTTP methods, status codes, headers | F | `BE Api/Infrastructure/Http.cs` (`GlobalExceptionHandler`: 400/401/403/404/409/422/429/503), `Controllers/*` (POST 201+Location, PUT, 204) | Integration tests assert status codes in `AuthTests`, `OrderTests` |
| **401 vs 403, 400 vs 422** (kit's own example question) | F | 401 = no/invalid identity (`AuthController`), 403 = identity ok but role/ownership denied (`Policy="Admin"`), 400 = malformed/invalid input (FluentValidation), 422 = valid shape but breaks a business rule (`DomainException`, idempotency key reuse) | `Protected_endpoints_need_a_token_and_admin_endpoints_need_the_admin_role` |
| Browser rendering (DOM, CSSOM, reflow) | C | `FE shared/*` + `OnPush` everywhere; `track` in every `@for` avoids DOM rebuilds | Angular DevTools profiler; Chrome Performance tab |
| **CORS and same-origin policy** | C | `BE Api/Program.cs` (explicit allow-list, credentials), but the *better* answer: the dev proxy `frontend/proxy.conf.json` and `nginx.conf` make API and UI **same-origin**, so CORS is not needed | `curl` with a different `Origin` header; read `Program.cs` CORS comment |
| Cookies, sessions, localStorage | C | Access token in a signal (memory); refresh token in an **httpOnly, SameSite=Strict, Path-scoped cookie** (`AuthController.CookieOptions`, `FE core/auth.store.ts`) | `Refresh_token_lives_in_an_httpOnly_cookie_not_in_the_body` |
| **"Where do you store a JWT, and why not localStorage?"** | C | `FE core/auth.store.ts` doc comment + `docs/06-interview-qa.md` | Open devtools -> Application: no token in storage |

## HTML & CSS

| Skill | Lvl | Where to look |
|---|---|---|
| Semantic HTML, accessibility (ARIA, keyboard) | F/C | `FE app.ts` (`<header>`, `<nav>`, `<main>`), `aria-label`, `role="alert"`, `aria-live` toast host, `:focus-visible` outlines in `styles.scss` |
| Box model, Flexbox/Grid, responsive, mobile-first | F/C | `styles.scss` (`.grid` uses `auto-fill/minmax`, `@media (max-width: 640px)`) |
| Design tokens / CSS architecture | A | `:root` custom properties + automatic dark mode via `prefers-color-scheme` in `styles.scss` |
| Tailwind | C | Not used on purpose (plain CSS + tokens keeps the focus on Angular/.NET). Exercise 7.18 converts it. |

## JavaScript / TypeScript

| Skill | Lvl | Where to look |
|---|---|---|
| Closures, scope | F | `FE core/toast.service.ts` (`setTimeout` closes over `id`), `auth.store.ts` (`refreshInFlight$` shared closure state) |
| **Event loop, microtasks vs macrotasks** | C | `docs/03-angular-concepts.md` § Event loop (includes the classic "order these logs" puzzle with an answer) |
| Promises, async/await, error handling | F | `FE app.config.ts` (`firstValueFrom` in the app initializer); RxJS equivalents of "3 parallel calls, one may fail" in `03-angular-concepts.md` |
| `this`, bind, prototypes | C | Why Angular code uses `inject()` + arrow-free classes: `docs/03` § "this & classes" |
| Array/object methods | F | `cart.store.ts` (`map`, `filter`, `reduce`, spread for immutable updates) |
| ES modules, bundling, tree shaking | C | `FE app.routes.ts` -> every page is a lazy chunk; `ng build` output lists chunk sizes (run `ng build` to see the per-route chunk sizes) |
| **TypeScript (types, generics, narrowing, utility types)** | C | `FE core/models.ts` (API contract types, union types for `OrderStatus`), `Pick<Product,...>` in `cart.store.ts`, `CursorPage<T>` generic |
| Debugging with devtools | F | Exercises 7.1-7.8 are "find the bug using devtools / logs / a failing test" |
| **Debounce / throttle** (kit interview Q) | C | `product-list.page.ts`: `debounceTime(300)` + `switchMap`; hand-written version in `03` |

## Frontend framework (Angular instead of React)

| Kit skill | Lvl | Angular equivalent -> where |
|---|---|---|
| Components, props, state, JSX | F | Standalone components, `input()`/`output()`, signals -> `FE features/catalog/product-card.ts` |
| Hooks (`useState/useEffect/useMemo/useCallback`) | C | `signal` / `effect` / `computed` (memoised, auto-tracked deps - no dependency-array bugs) -> `cart.store.ts`, `orders.page.ts` |
| Custom hooks & composition | C | Injectable stores/services + `inject()` function composition -> `AuthStore`, `RealtimeService` |
| State management (Context/Zustand/Redux) | C | Signal stores (`CartStore`, `AuthStore`). "When would you *not* reach for a global store?" -> `03` § State |
| **Server state vs client state** | C | Products/orders are fetched via `ApiService`, never mirrored in a global store; cart & session are client state |
| Routing (guards, redirect target) | F | `app.routes.ts`, `core/guards.ts`, `login.page.ts` (`returnUrl`) |
| Forms & validation | C | `login.page.ts`, `register.page.ts` (typed reactive forms; server errors rendered back) |
| Next.js / SSR / SSG / ISR / hydration | C/A | Not included (SPA). Angular SSR + hydration is Exercise 7.19; trade-off table in `03` § Rendering strategies |
| Performance (Core Web Vitals, code splitting, lazy) | C | Lazy routes, `OnPush`, `track`, debounced search, cursor paging (never loads 10k rows) |
| Component testing | C | `FE features/catalog/product-card.spec.ts`, `core/auth.interceptor.spec.ts`, `cart.store.spec.ts` |
| Vue/Angular basics | Opt | This entire folder |

## Backend

| Kit skill | Lvl | Where to look | Proof |
|---|---|---|---|
| Runtime & event loop ("why is CPU work slow?") | C | .NET uses a **thread pool + async I/O**: `await` releases the thread, CPU-bound work still blocks one. `docs/02` § async | - |
| Framework layer; "where does business logic live?" | C | **Clean Architecture**: `Domain` (rules) <- `Application` (use-cases) <- `Infrastructure`/`Api` (details). Logic is in `Order.cs`, `OrderService.cs`, never in controllers | Controllers are 1-3 lines each |
| **REST design (resources, versioning, idempotency)** | F | `Controllers/*`, URL versioning `/api/v1/...` (`Asp.Versioning`), `Idempotency-Key` on `POST /orders` | `Retrying_with_the_same_idempotency_key_creates_one_order` |
| GraphQL, N+1 | C | GraphQL not included (Exercise 7.20: HotChocolate). N+1 is covered in EF form: `OrderService.ListAsync` (`Include` + `AsSplitQuery`), `ProductService` projection with `Select` | `02` § N+1 |
| **Authentication (sessions, JWT, OAuth/OIDC), refresh rotation** | C | `Application/Auth/AuthService.cs`, `Infrastructure/Security/SecurityServices.cs`, `Api/Program.cs` (JwtBearer) | `Refresh_rotates_and_REUSE_of_an_old_token_revokes_the_whole_family` |
| Authorisation (RBAC, row-level) | C | Role policy `"Admin"`; **ownership enforced inside the query** (`OrderService.Visible()`); **tenant filter** (`AppDbContext.TenantFilter`) | `A_customer_cannot_read_another_customers_order...`, `Each_tenant_only_sees_its_own_catalogue` |
| Password handling | F | `Pbkdf2PasswordHasher` + timing-equalised unknown-user path in `AuthService.LoginAsync` | `PasswordHasherTests`, `Wrong_password_and_unknown_user_give_the_same_401` |
| Input validation & sanitisation | F | FluentValidation validators next to each use-case; `[ApiController]` model-binding 400s; LIKE-wildcard escaping in `ProductService.EscapeLike` | `Search_is_prefix_based_and_survives_LIKE_wildcards_and_SQL_injection_attempts` |
| File uploads / object storage | C | Not included (Exercise 7.17: streamed upload to Azure Blob/S3-compatible). Design notes in `05` | - |
| **Background jobs & queues** ("email slows signup - fix it") | C | **Transactional outbox**: `Messaging/Outbox.cs` (`OutboxDispatcher` + `OutboxWorker`), rows written in the order's transaction | `Outbox_message_is_written_with_the_order_and_dispatched_to_the_user` |
| WebSockets / real-time | C | `Api/Hubs/OrderHub.cs` + `FE core/realtime.service.ts` | Pay an order in the UI: status flips without refresh |
| Caching | C | `CatalogCache` (`ProductService.cs`): cache-aside + per-tenant change-token invalidation; 5-min report cache | `05` § Caching (what can go wrong) |
| Rate limiting | C | `Api/Program.cs`: fixed window on `/auth` per IP, token bucket per user globally, `Retry-After` | Exercise 7.9 |
| Logging, error tracking, observability | C | Serilog structured logs, correlation id middleware, `/health/live` + `/health/ready`, problem+json with `code` | Every response carries `X-Correlation-Id` |

## Databases (SQL Server)

| Kit skill | Lvl | Where to look |
|---|---|---|
| Relational modelling & normalisation | F | `AppDbContext.OnModelCreating` + ER diagram in `04-sql-concepts.md` (note the **deliberate** denormalisation: `OrderItem.ProductName/UnitPrice` snapshot, `OrderItem.TenantId`) |
| **SQL: joins, aggregates, subqueries, window functions** | F | `sql/01_window_functions_and_joins.sql` (top-N per group, RANK/DENSE_RANK, LAG, running totals, NOT EXISTS) + live in `Application/Reports/ReportService.cs` |
| **Indexes & query plans** | C | `sql/02_indexes_and_query_plans.sql` prints logical reads before/after: keyset vs OFFSET **461 -> 6**, covering index **1,362 -> 76**, sargable range, filtered index |
| **Transactions, ACID, isolation levels** ("last item") | C | `OrderService.CreateOrderTransactionAsync`; `sql/03_transactions_locking_isolation.sql` | `Forty_buyers_race_for_ten_items_exactly_ten_succeed...` |
| N+1 & eager loading | C | See backend row above |
| ORMs; "when to drop to raw SQL" | C | EF everywhere; `ReportService` uses `SqlQuery<T>` with parameters because window functions + top-N are clearer in SQL |
| **Migrations & schema evolution** ("add NOT NULL to 50M rows") | C | `Infrastructure/Persistence/Migrations/*`, `sql/04_safe_migrations.sql` (expand -> backfill in batches -> contract) |
| NoSQL / "Postgres or Mongo?" | C | `04` § "Why not MongoDB here" |
| Connection pooling | C | `04` § Pooling; EF `AddDbContext` + `EnableRetryOnFailure`; why `DbContext` is scoped and short-lived |
| Backups & restore | C | `sql/05_backup_restore.sql` (RPO/RTO, point-in-time restore to a *copy*) |

## API & Integration

| Kit skill | Lvl | Where to look |
|---|---|---|
| OpenAPI / Swagger | C | Swagger UI at `/swagger` (Development) from `Program.cs` |
| **Pagination: cursor vs offset** | C | `Application/Common/Cursor.cs`, `ProductService.QueryAsync`; measured in `sql/02` |
| Error contract | C | RFC 9457 problem+json + stable `code` (`GlobalExceptionHandler`); consumed by `FE core/http.interceptors.ts#problemOf` |
| **Webhooks (delivery, retries, signature verification)** | C | `Application/Payments/PaymentWebhookService.cs` (HMAC verify, replay window, dedupe table, outbox), `HmacWebhookSignatureVerifier` | `The_same_event_delivered_twice_marks_paid_once...`, `WebhookSignatureTests` |
| Third-party integration & provider down | C | `Infrastructure/Payments/PaymentGateways.cs`: typed `HttpClient` + `AddStandardResilienceHandler()` (timeout, retry w/ jitter, circuit breaker); 503 + `Retry-After` mapping |

## Architecture

| Kit skill | Lvl | Where to look |
|---|---|---|
| Separation of concerns & layering | C | The four projects and their reference direction (Domain has **zero** dependencies) |
| **Monolith vs microservices** | C | ShopFlow is a **modular monolith** on purpose. `05` § "Should we split?" |
| System design fundamentals; scaling; statelessness | C | `docs/05-system-design.md`: the API keeps **no state in memory that matters** (JWT is self-contained, refresh tokens in SQL, cache is disposable) -> run N copies behind a load balancer |
| CAP & consistency | A | `05` § CAP: where ShopFlow chooses consistency (stock = SQL, single writer row) vs eventual consistency (reports cache, outbox) |
| Event-driven & message queues | A | Outbox pattern, at-least-once + idempotent consumers; swap `OutboxWorker` for Service Bus/RabbitMQ in `05` |
| **Multi-tenancy** | A | EF global query filters + `TenantId` on every row (shared schema); alternatives table in `05` |
| Designing for failure | A | Retries w/ backoff (EF `EnableRetryOnFailure`, resilience handler), timeouts, circuit breaker, idempotency, outbox, compensation (`RefundRequired`) |

## Security

| Kit skill | Lvl | Where to look |
|---|---|---|
| OWASP Top 10 | C | `docs/06-interview-qa.md` § Security maps each OWASP item to a concrete control in this repo |
| SQL injection | F | EF parameterises LINQ; `SqlQuery<T>` takes `FormattableString` -> parameters, never concatenation. Test posts `'; DROP TABLE Products;--` as a search |
| XSS & CSRF | C | Angular auto-escapes interpolation (no `innerHTML`/`bypassSecurityTrust*` used); refresh cookie is `SameSite=Strict`, `/refresh` accepts no ambient state change; access token is a header (not auto-sent) so CSRF cannot use it |
| Secrets management | F | `Program.cs` **fails fast** if `Jwt:SigningKey` is short/missing; compose/CI read secrets from env; dev key is clearly a dev key |
| HTTPS/TLS/secure headers | C | `SecurityHeadersMiddleware` (CSP, nosniff, frame deny, referrer, no-store), HSTS + redirect (configurable) |
| Dependency / supply chain | C | CI runs `dotnet list package --vulnerable` and `npm audit`; lock files pinned (`npm ci`) |

## Testing

| Kit skill | Lvl | Where to look |
|---|---|---|
| Unit tests & the pyramid | F | `backend/tests/ShopFlow.UnitTests` (30 fast tests: domain rules, HMAC, hashing, cursor, validators) |
| **Integration tests with a real database** | C | `backend/tests/ShopFlow.IntegrationTests` (24 tests, real SQL Server, fresh DB per run) - *"mocks hide the bugs that reach production"* |
| E2E (Playwright/Cypress) | C | Not included. Exercise 7.16 adds Playwright against `docker compose up` |
| Test data, fixtures, factories | C | `DbSeeder` + `ApiFactory`/`TestExtensions` helpers |
| **Mocking & DI** ("test without hitting the payment provider") | C | `IPaymentGateway` interface + `FakePaymentGateway`; `RecordingNotifier` replaces SignalR in tests via `ConfigureServices`; `FakeTimeProvider` for time travel |
| Frontend tests | C | 9 Vitest tests incl. the single-flight refresh test |

## DevOps

| Kit skill | Lvl | Where to look |
|---|---|---|
| Git workflow, CI/CD | F/C | `.github/workflows/ci.yml` (build, test with real SQL service, vuln scan; deploy sketch) |
| **Docker** ("write a Dockerfile, now make it smaller") | C | `backend/Dockerfile` (multi-stage, layer-cached restore, non-root, runtime-only image), `frontend/Dockerfile` (build -> nginx) |
| Twelve-factor config | C | Everything is configured by env vars / `appsettings` layering (`ConnectionStrings__Default` etc.); no environment-specific code |
| Deployment platforms / cloud basics | C | `docker-compose.yml` is the topology; `05` maps it onto Azure (App Service/Container Apps + Azure SQL + Key Vault + Front Door) |
| Monitoring & alerting | C | Health endpoints for probes; structured logs; what to alert on in `05` |
| Zero-downtime deploys & rollback | A | Readiness probe, additive migrations (`sql/04`), roll back = redeploy previous image SHA |

## Practice

| Kit skill | Lvl | Where to look |
|---|---|---|
| Reading & debugging unfamiliar code | C | `docs/07-exercises.md` 7.1-7.8: "break it / find the bug" tasks |
| Docs & READMEs | C | You are reading it |
| Estimating & scoping | C | `05` has back-of-envelope capacity maths you can reuse |
| Working with designs | C | n/a (no Figma here) |
| DSA fundamentals | C | Not the point of this repo; `Cursor`, `HashSet` dedupe, `GroupBy` merge in `OrderService` show *applied* complexity thinking |

---

## Honest gaps (things in the kit that ShopFlow does *not* implement)

Listed so you know where to extend it; each has an exercise: **OAuth/OIDC login** (7.14), **Redis** (7.11), **GraphQL** (7.20),
**file upload / object storage** (7.17), **Playwright E2E** (7.16), **SSR/hydration** (7.19), **Tailwind** (7.18),
**full-text search** (7.13), **RLS in SQL Server** (7.12), **Stripe for real** (7.15).