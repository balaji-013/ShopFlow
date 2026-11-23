# ShopFlow - a real-world full-stack project for learning what senior .NET developers must know

A multi-tenant online store built with **ASP.NET Core 9 + EF Core + SQL Server** and **Angular 21 (signals, RxJS, SignalR)**.
It is deliberately small enough to read in a day and real enough to contain the problems that decide senior interviews:
**money, concurrency, auth, multi-tenancy, background work, real-time, caching, failure handling, performance.**

It is mapped, skill by skill, to *Full-Stack Complete Resource Kit 2026 (Let's Code)* - see [`docs/00-concept-map.md`](docs/00-concept-map.md).

| What you will see working | Where |
|---|---|
| 40 buyers race for 10 items - exactly 10 win, stock never negative | `OrderService` + test `Forty_buyers_race_for_ten_items...` |
| Double-click / retry creates ONE order (Idempotency-Key) | `OrderService.PlaceAsync` |
| Duplicate payment webhook is harmless; forged ones rejected (HMAC) | `PaymentWebhookService` |
| JWT in memory + rotating httpOnly refresh cookie + theft detection | `AuthService`, `auth.store.ts` |
| Tenant A can never see tenant B (global query filters) | `AppDbContext`, `TenantIsolationTests` |
| Transactional outbox -> background worker -> SignalR push | `Messaging/Outbox.cs`, `realtime.service.ts` |
| Cursor pagination (461 -> 6 page reads vs OFFSET) | `Cursor.cs`, `sql/02_*.sql` |
| Window-function report, optimistic concurrency, resilient HTTP client | `ReportService`, `ProductService`, `PaymentGateways` |

## Repository map

```
ShopFlow/
├─ backend/                     .NET 9 solution (Clean Architecture)
│  ├─ src/ShopFlow.Domain          entities + business rules (no dependencies)
│  ├─ src/ShopFlow.Application     use-cases, validators, interfaces
│  ├─ src/ShopFlow.Infrastructure  EF Core, migrations, JWT/HMAC/hashing, outbox worker, HTTP gateways
│  ├─ src/ShopFlow.Api             controllers, middleware, SignalR hub, Program.cs
│  └─ tests/                       30 unit tests + 24 integration tests (real SQL Server)
├─ frontend/                    Angular 21 SPA (standalone components, signals) + 9 Vitest tests
├─ sql/                         00 load 100k orders | 01 window functions | 02 indexes & plans | 03 locking | 04 safe migrations | 05 backup
├─ docs/                        concept map, architecture, .NET, Angular, SQL, system design, interview Q&A, exercises
├─ docker-compose.yml           sql + api + nginx(web)  ->  http://localhost:8080
└─ .github/workflows/ci.yml     build + test (real SQL service) + vulnerability scans
```

## Run it

### Option A - local development (needs .NET 9 SDK, Node 22, SQL Server LocalDB or any SQL Server)
```powershell
# 1) API  (creates + migrates + seeds the "ShopFlow" database on first start; http://localhost:5080/swagger)
cd backend\src\ShopFlow.Api
dotnet run --launch-profile http

# 2) Web  (proxies /api and /hubs to the API -> same origin, no CORS)
cd frontend
npm install
npm start                      # http://localhost:4200
```
Not using LocalDB? Change `ConnectionStrings:Default` in `appsettings.Development.json`, or set the env var `ConnectionStrings__Default`.

**Demo logins** (shown on the login page)

| Store | Customer | Admin |
|---|---|---|
| Acme Electronics | `demo@acme.test` / `Customer#12345` | `admin@acme.test` / `Admin#12345` |
| Globex Gadgets | `demo@globex.test` / `Customer#12345` | `admin@globex.test` / `Admin#12345` |

### Option B - everything in containers
```bash
docker compose up --build      # then open http://localhost:8080
```

### Tests
```powershell
cd backend ; dotnet test                      # 30 unit + 24 integration (creates/drops a throwaway database per run)
cd frontend ; npm test                        # 9 component/service tests (Vitest)
```
Integration tests use LocalDB by default; point them at any SQL Server with `TEST_SQL_SERVER="Server=...;User Id=sa;Password=...;TrustServerCertificate=True"` (CI does).

### SQL lab
```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d ShopFlow -E -i sql\00_load_sample_data.sql     # ~40 s: 1,000 users, 100,000 orders
sqlcmd -S "(localdb)\MSSQLLocalDB" -d ShopFlow -E -i sql\02_indexes_and_query_plans.sql
```
(`dotnet ef database drop` removes the lab data; the API recreates and re-seeds on next start.)

## A 5-day way to use this

| Day | Do | Read |
|---|---|---|
| 1 | Run it. Click every screen with devtools open. Read the architecture doc and trace one request. | `docs/01-architecture.md` |
| 2 | .NET: read `OrderService`, `AuthService`, `Program.cs`, then run the tests. Do exercises 7.1-7.3 (break the concurrency code, watch tests fail). | `docs/02-dotnet-concepts.md` |
| 3 | SQL: load 100k orders and run files 01-04, reading each plan. | `docs/04-sql-concepts.md` |
| 4 | Angular: read `auth.store.ts`, interceptors, `product-list.page.ts`; do 7.4 and 7.8. | `docs/03-angular-concepts.md` |
| 5 | System design + mock interviews using the Q&A (answer aloud, then check). | `docs/05`, `docs/06` |

Then extend it (exercises 7.11-7.20): Redis, row-level security, OAuth, Playwright, file uploads, GraphQL, SSR.

## Honest scope notes

* The "payment provider" is simulated (`FakePaymentGateway` + a dev-only endpoint that sends a *correctly signed* webhook through the real handler). Swap in Stripe test mode in exercise 7.15.
* The outbox worker polls every 2 s - fine for learning and for modest scale; at scale you would use a broker (Service Bus/RabbitMQ) or CDC.
* The dev signing keys in `appsettings.Development.json` and `docker-compose.yml` are **demo values**. Real secrets come from a vault/environment; the API refuses to start without a proper key.
* Not included (on purpose, each has an exercise): Redis, OAuth/OIDC, GraphQL, file uploads, Playwright, SSR, Tailwind, SQL Row-Level Security.