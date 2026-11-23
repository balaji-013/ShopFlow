# Interview Q&A - every question from the kit's *Interview Prep* sheet, answered through ShopFlow

How to use: cover the answer, say it out loud in ~60-90 seconds, then check. Each answer ends with **-> proof** (a file/test you can show on screen).
React questions in the kit are answered with their **Angular** counterparts. Extra .NET/C# questions are at the end.

---
## Web fundamentals

**What happens when you type a URL and press Enter?**
DNS lookup (cache -> OS -> resolver) -> TCP handshake -> TLS handshake -> HTTP request (maybe via CDN/LB) -> server routes it -> response -> browser parses HTML to the **DOM**, CSS to the **CSSOM**, merges into a render tree, **layout -> paint -> composite**; JS runs and can change all of it. Go as deep as they let you; offer to drill into any step. -> `docs/01-architecture.md` traces it through this exact app.

**Explain CORS. Why does the browser block the request but `curl` works?**
The **same-origin policy** is enforced *by browsers* to stop page A from reading responses of site B using the user's cookies. CORS is the server's opt-in: it replies with `Access-Control-Allow-Origin` (and for non-simple requests answers the `OPTIONS` **preflight**). `curl` is not a browser and has no policy. The fix is a server-side allow-list - never `*` with credentials - or, better, avoid cross-origin entirely with a reverse proxy. -> `Program.cs` (CORS), `proxy.conf.json`, `nginx.conf`.

**401 vs 403? 400 vs 422?**
401: *who are you?* - missing/invalid credentials. 403: I know who you are, you may not do this. 400: the request is malformed/invalid input (validation). 422: well-formed but violates a business rule (e.g. illegal state transition). Also 404 to avoid revealing existence of someone else's resource; 409 for conflicts with current state (stock, concurrency); 429 rate limit. -> `GlobalExceptionHandler`, `Protected_endpoints_need_a_token...`.

**How does browser caching work, and how do you bust it?**
`Cache-Control` (max-age, no-store, immutable), validators `ETag`/`Last-Modified` -> conditional requests (`304`). Bust by **changing the URL**: bundlers emit content-hashed filenames, so they're cached for a year, while `index.html` (which references them) is `no-cache`. "Users see stale JS" = `index.html` was cached or the bundle name didn't change. -> `nginx.conf`, API sends `no-store` for authenticated data.

## JavaScript / TypeScript

**Closures with a practical example.** A function that remembers variables from the scope it was created in, even after that scope finished. `debounce` keeps a private timer id; `ToastService.show` keeps `id` for the delayed dismiss. -> `03-angular-concepts.md` §3.
**Order these logs (timeout, promise, sync).** Sync -> microtasks (promises/`await`) -> macrotask (timers). Worked example with answer `1 5 7 3 4 6 2`. -> §12.
**`==` vs `===`.** `==` coerces types (`0 == ''`, `null == undefined`); use `===` always. Coercion bites on form input strings vs numbers and on `if (x)` with `0`/`''`.
**`var`/`let`/`const`.** Scope (function vs block), hoisting + temporal dead zone, reassignability (`const` binding not immutability).
**`this`: regular function vs arrow.** Regular: set by *call site* (method call, `call/apply/bind`, `new`, or undefined/global). Arrow: lexical `this` of where it was defined. Bug: passing `obj.method` as a callback loses `this`.
**Debounce / throttle from scratch.** Code in §3 of the Angular doc; debounce = last call after quiet period, throttle = at most once per window. RxJS: `debounceTime`, `throttleTime`. -> `product-list.page.ts`.
**Deep clone edge cases.** `structuredClone` (Dates, Maps, Sets, cycles OK; functions/DOM nodes throw); `JSON` round-trip loses `undefined`, functions, Dates -> strings, throws on cycles; class prototypes are lost.
**Event delegation.** One listener on an ancestor reading `event.target`; works because events bubble; cheaper and handles dynamic children.
**`type` vs `interface`.** Both describe object shapes; interfaces can be re-opened/merged and `extends`; `type` can alias unions/tuples/mapped types. Pick one convention. -> `core/models.ts`.
**Generics - write one.** `type CursorPage<T> = { items: T[]; nextCursor: string | null }`; `function first<T>(xs: T[]): T | undefined`. Constrain with `extends` when you access members. -> `models.ts`.
**Type an API response with nullable fields safely.** Model reality (`paidAt: string | null`); let the compiler force narrowing; validate at the boundary (a runtime schema like Zod, or generated types from OpenAPI) because TS types vanish at runtime; never `as` your way out.

## Angular (in place of the React questions)

**Virtual DOM & reconciliation?** React diffs a virtual tree. Angular has no VDOM: templates compile to instructions that update only the bindings that changed (signals make this fine-grained). The shared idea: avoid touching the DOM unless data changed.
**"Why does this effect run twice / loop forever?"** React: missing/wrong dependency array, StrictMode double-invoke. Angular: an `effect` that **writes a signal it also reads** loops; effects auto-track what they read, so keep them for *side effects* (persisting the cart) and use `computed` for derived values. -> `CartStore` (effect only persists).
**`useMemo`/`useCallback` - when harmful?** Memoisation costs memory/comparisons; premature use adds noise. Angular's `computed` is memoised automatically and cheap; don't add manual caching without a measurement. Pure pipes and `OnPush` play the same role.
**What is the `key` prop for; why not index?** Identity for reconciliation so state/DOM follow the *item*. With index keys, inserting at the top re-attaches state to the wrong rows. Angular: `@for (p of items; track p.id)` - **required**, use a stable id.
**Controlled vs uncontrolled components?** React: value owned by state vs by the DOM. Angular: reactive forms (model-driven, value owned by the `FormControl`) vs template-driven (`ngModel`, DOM-driven). Reactive scales and is easier to test. -> `login.page.ts`.
**Share state between distant components?** Lift state up (short distance) -> **injectable store service with signals** (`CartStore`) -> NgRx/signal-store for large complex apps. Not a global store for local UI state.
**Server state vs client state?** Server state is a *cache of remote truth* (can go stale, needs refetch/invalidation); client state is owned by the UI (cart, open dialog). Mixing them causes stale-data bugs. Here: products/orders are fetched via `ApiService` and held in the page; the cart/session are client stores. (React uses TanStack Query for the former; Angular teams use `httpResource`/RxJS or NgRx data.)
**Server component vs client component?** React-specific. Angular equivalents: SSR + hydration for first paint/SEO, `@defer` for lazy client islands. Rule: do on the server what needs secrets/data and no interactivity.
**Test a component so it catches a real regression.** Render with real inputs, act like a user (click), assert **visible behaviour** (emitted event, disabled state, text), not implementation. -> `product-card.spec.ts`; and for logic that really matters, the HTTP-level `auth.interceptor.spec.ts`.

## Frontend

**4-second page load - diagnose.** Measure first (Lighthouse / Network + Performance tabs, field data). Check in order: TTFB/API latency -> render-blocking resources and **bundle size** -> images (size, format, lazy) -> fonts -> main-thread JS (long tasks) -> layout shifts. Fix the biggest slice; re-measure. -> lazy routes, budgets in `angular.json`.
**Core Web Vitals; improve LCP.** LCP (loading), INP (interactivity), CLS (stability). LCP: find the LCP element (usually hero image/heading); preload it, right-size + modern format + CDN, reduce TTFB, remove render-blocking CSS/JS, SSR/prerender if the shell is empty.
**Make this component accessible.** Semantic elements first (`button`, `nav`, `label`), keyboard operability + visible focus, accessible names (`aria-label`), announce dynamic changes (`aria-live` toast), colour contrast, don't rely on colour alone, test with a screen reader. -> `app.ts`, `toast-host.ts`, `styles.scss`.
**3 MB bundle - reduce.** Analyse (`source-map-explorer`/stats), lazy-load routes (`loadComponent`), drop/replace heavy deps (moment -> date-fns/Intl), ensure tree-shakeable imports, `@defer`, compress (brotli), remove duplicates, enforce `budgets` in CI.

## Backend

**Design the REST endpoints for this feature.** Nouns, plural, hierarchical: `GET/POST /products`, `GET/PUT /products/{id}`, `POST /orders` (+`Idempotency-Key`), `GET /orders?cursor=`, **actions as sub-resources/verbs where it's not CRUD**: `POST /orders/{id}/cancel`, `POST /products/{id}/stock-adjustments`. Correct status codes, versioning, pagination, filtering, consistent errors. -> `Controllers/*`.
**How do you store passwords?** Never plain, never reversible encryption, never fast hashes. A slow, salted, adaptive hash: argon2id/bcrypt/scrypt/**PBKDF2** with tuned iterations; per-password salt; rehash when parameters improve; same-time response for unknown users. -> `Pbkdf2PasswordHasher`, `AuthService.LoginAsync`.
**Sessions or JWT?** Sessions (server-side state, instantly revocable, need a shared store when scaled) vs JWT (stateless verification, great for APIs/multi-service, hard to revoke before expiry). Both valid. ShopFlow: **short JWT + rotating refresh token in SQL** - stateless hot path, revocable long-lived credential. -> `docs/01` flow 1.
**Where do you store the JWT on the client, and why not localStorage?** localStorage is readable by any script: one XSS steals the token. Keep the **access token in memory**, the **refresh token in an httpOnly, Secure, SameSite cookie** (JS-invisible), accept the cookie's CSRF surface and cover it with SameSite + a path-scoped, header-less endpoint. Trade-off: the access token is lost on reload -> silent refresh at startup. -> `auth.store.ts`, `AuthController`.
**Refresh tokens and rotation?** Short access token expires; the refresh token obtains a new one. **Rotation:** each refresh issues a *new* refresh token and invalidates the old; if an old one is **reused** -> assume theft, revoke the whole token family. Store only hashes. Concurrent refresh must be atomic (`UPDATE ... WHERE UsedAt IS NULL`). -> `AuthService.RefreshAsync`, test `Refresh_rotates_and_REUSE...`.
**Endpoint takes 3 s - fix.** Don't guess: get a trace/log timing, find the slow part. Usual order: N+1 / too many queries -> missing index / bad plan (actual plan, logical reads) -> fetching too much (projection, paging) -> sync calls to slow services (timeouts, parallelise, queue) -> repeated work (cache) -> CPU hot spot (profiler). Re-measure. -> `sql/02`.
**Slow third-party API inside the request.** Timeout + retry-with-jitter + **circuit breaker**; if the user doesn't need the result now, **move it to a queue** and respond fast; cache results; degrade gracefully. -> `PaymentGateways.cs` (resilience handler), outbox.
**Email slows signup.** Don't send inside the request. Write an **outbox** row in the same transaction and let a worker send; signup returns immediately and the e-mail is retried independently. -> `Messaging/Outbox.cs`.
**Someone hammers signup.** Rate limit per IP + per account (`429`, `Retry-After`), CAPTCHA/proof-of-work on suspicion, uniform responses (no enumeration), monitoring/alerts, block abusive ranges at the edge/WAF, cheap rejection before expensive hashing. -> `Program.cs` `"auth"` limiter.
**How do you validate input, and where?** **Server-side always** (clients can be bypassed); client-side for UX. Validate shape/range early (FluentValidation, model binding), business rules in the domain, constraints in the DB. Whitelist, normalise, and encode on output. -> validators + `Order.Create`.

## Databases

**Design the schema for this product; justify each table.** See ER diagram + justifications in `docs/01-architecture.md` and `docs/04-sql-concepts.md` §1 (tenant, user, category, product, order + order_item with snapshots, refresh_token, outbox, idempotency, webhook dedupe, audit).
**Top three orders per customer.** `ROW_NUMBER() OVER (PARTITION BY UserId ORDER BY Total DESC, Id)` in a CTE, `WHERE rn <= 3`. -> `sql/01` Q1.
**This query is slow; here is the plan.** Look for scans vs seeks, key lookups, sorts/spills, big gaps between estimated and actual rows, implicit conversions; compare **logical reads**. Fix with the right composite index (equality then order), covering `INCLUDE`, sargable predicates, fresh statistics. -> `sql/02` with measured before/after.
**N+1 - show one and fix it.** Loading a list of orders then touching `order.Items` per order = 1 + N queries. Fix: `Include` (+`AsSplitQuery`), projection with `Select`, or a join. -> `OrderService.ListAsync`.
**Two users buy the last item at once.** Naive read-check-write oversells. Use one **atomic conditional UPDATE** (`Stock >= q`) and check rows affected; lock ordering to avoid deadlock; unique/CHECK constraints as safety; rowversion where users edit. -> test `Forty_buyers_race...`, `sql/03`.
**Add a NOT NULL column to a 50M-row table.** Expand -> backfill in small batches -> contract. Never one big blocking `UPDATE`; keep the previous app version working. -> `sql/04`.
**PostgreSQL or MongoDB? Defend it.** Relational by default for transactions, relationships and reporting; Mongo when data is genuinely document-shaped with one dominant access pattern and flexible schema. Here: money + stock + joins -> relational. (Postgres vs SQL Server is a platform choice; the reasoning is identical.) -> `04` §1.
**Cursor or offset pagination?** Offset is simple and allows jumping to page N, but cost grows with N and results shift when data changes. **Keyset/cursor** is O(log n) and stable; needs a unique ordered key and gives next/prev only. Measured: 461 vs 6 reads. -> `Cursor.cs`, `sql/02`.

## Security

**Name three vulnerabilities in this code.** Practise on snippets; typical: SQL built by string concat, no authorisation check on `GET /orders/{id}` (IDOR/BOLA), secrets in source, token in localStorage, missing rate limit, verbose errors leaking stack traces, `bypassSecurityTrust*`. Map each to OWASP below.
**How does the framework protect against XSS and where doesn't it?** Angular escapes interpolation and sanitises `[innerHTML]`/URLs/styles. It doesn't protect you when you **bypass** the sanitizer, write to the DOM directly, trust third-party libs, or render attacker-controlled data in a context Angular can't know (some script/URL sinks). Add CSP. (React: same story with `dangerouslySetInnerHTML` and `href="javascript:"`.)
**What is CSRF; when do you need protection?** An attacker page makes the victim's browser send a request that carries **ambient credentials** (cookies). You need protection when auth is by cookie. Defences: `SameSite` cookies, anti-forgery tokens, custom headers/`Origin` checks. ShopFlow authenticates API calls with an `Authorization` header (not ambient) and its one cookie is `SameSite=Strict` + path-scoped.
**Where do production secrets live?** Not in git or the image. Environment variables / a secret manager (Key Vault) injected at runtime, rotated, least-privilege, different per environment; app fails fast if missing. Scan history for leaks (a committed key is compromised forever). -> `Program.cs` fail-fast, `docker-compose.yml`.
**How do you stop user A reading user B's data?** Enforce at the **data layer**, not just the route: the query itself includes the owner/tenant (`Visible()`, global tenant filter, optionally SQL RLS); return 404; test it. -> `OrderService.Visible`, tests in `TenantIsolationTests`.

### OWASP Top 10 (2021) -> controls in this repo
| Risk | Control |
|---|---|
| A01 Broken access control | Role policies, ownership in queries, tenant filter, 404 for others, tests |
| A02 Cryptographic failures | PBKDF2 hashing, HS256 key >= 32 chars, TLS/HSTS, refresh tokens stored hashed, HMAC webhook signatures |
| A03 Injection | EF/parameterised SQL, `SqlQuery` interpolation = parameters, LIKE-escape, Angular escaping |
| A04 Insecure design | Idempotency, state machine, rate limits, outbox, threat-aware flows (rotation + reuse detection) |
| A05 Security misconfiguration | Fail-fast config, secure headers, Swagger off in prod, no detail in 500s, explicit CORS list |
| A06 Vulnerable components | `dotnet list package --vulnerable`, `npm audit`, lock files |
| A07 Auth failures | No user enumeration, rotation, short TTL, rate limit on `/auth`, password length policy |
| A08 Software/data integrity | Signed webhooks, CI build, pinned deps, migrations reviewed |
| A09 Logging/monitoring failures | Structured logs, correlation ids, audit log table |
| A10 SSRF | No user-supplied URLs fetched server-side (if you add link previews: allow-list + block internal ranges) |

## Architecture

**Design a URL shortener / social feed / booking system.** -> `docs/05-system-design.md` parts A, B, C (with estimates and trade-offs).
**Should we split this into microservices?** Usually not yet - you'd trade in-process calls and ACID for network failures, eventual consistency and operational overhead. Split on measured scaling/ownership needs; keep module boundaries clean so you can later. -> `05` §G.
**Your app works on one server. Now run five.** Make it **stateless** (no in-memory sessions/uploads), shared cache/session store, distributed locks only if necessary, LB with health checks, backplane for WebSockets, migrations run once, config/secrets shared, rolling deploys. -> `05` §1 audit table.
**How would you add caching here, and what could go wrong?** Cache-aside with TTL + targeted invalidation; risks: stale data, invalidation bugs, stampede, penetration, hot keys, cold start, cross-user leakage. Never let the cache decide money. -> `05` §2, `CatalogCache`.
**10x traffic spike on checkout.** Queue to absorb, autoscale stateless tier, protect DB, rate limit + shed non-critical traffic, hot-row mitigation, idempotency, observability. -> `05` §4.
**Draw the infrastructure on a whiteboard.** Browser -> CDN/WAF -> LB -> N app instances -> Redis + SQL (primary + replica) -> queue -> workers; secrets vault, logs/metrics/traces, CI/CD pipeline. -> `docker-compose.yml` (topology), `05` §7 (Azure mapping).

## Testing & delivery

**What would you test in this function, and what would you not?** Behaviour and edge cases that matter (boundaries, invalid input, state transitions, error paths); not framework internals, trivial getters, or implementation details. Prefer fewer meaningful tests to coverage numbers. -> `DomainTests.cs`, `WebhookSignatureTests`.
**Tests pass locally, fail in CI.** Environment differences (versions, OS, timezone, locale), hidden dependence on local data/files/ports, test order/shared state, timing/races (sleeps), missing env vars/secrets, case-sensitive paths, parallelism. Make tests hermetic: fresh DB per run, fake clock, no sleeps. -> `ApiFactory`, `FakeTimeProvider`.
**How does a change get from your laptop to production?** Branch -> PR -> review -> CI (build, tests with real DB, vulnerability scan) -> merge -> build image tagged with SHA -> run migrations -> deploy (rolling/blue-green) gated by readiness -> smoke test -> monitor -> rollback = redeploy previous SHA. -> `.github/workflows/ci.yml`.
**Bad deploy, users affected - first five minutes.** **Mitigate before diagnosing**: roll back / flip the feature flag, confirm recovery on dashboards, tell stakeholders, then investigate with preserved logs and write a blameless post-mortem. -> `05` §5.

## Practice

**Repository you've never seen - find the bug.** Reproduce it, read the failing test/log, bisect (`git bisect`), trace from the symptom backwards, form one hypothesis at a time, add a failing test, fix, run the suite. Exercises 7.1-7.8 train this on ShopFlow.
**Walk me through your hardest project and one thing you'd change.** Use ShopFlow: the concurrency/idempotency story and the real bugs from `02-dotnet-concepts.md` §8. What I'd change: replace polling outbox with a broker, add Redis + SignalR backplane, RLS in SQL, Playwright E2E.
**How long would this feature take? Reason it through.** Break into tasks (API, data/migration, UI, tests, docs, deploy), flag unknowns (spike first), estimate ranges with a confidence level, add review/QA/rollout time, state assumptions, re-estimate as you learn.
**The design doesn't specify mobile behaviour.** Ask the designer/PM first; meanwhile choose a sensible responsive default (mobile-first, stack columns, touch targets >= 44 px), document the assumption, and make it easy to change.
**Explain a technical decision to a non-technical stakeholder.** Lead with the outcome and the risk/cost in their language: *"We save the order first and send the email afterwards, so a slow email service can never make a customer wait or lose their order."* Offer the alternative and why it's worse.

---
# Extra .NET / C# questions seniors get

| Question | Strong answer |
|---|---|
| `IEnumerable<T>` vs `IQueryable<T>` | `IEnumerable` runs in memory (LINQ-to-Objects, delegates). `IQueryable` builds an expression tree the provider (EF) translates to SQL. Calling `.ToList()`/`AsEnumerable()` too early pulls everything into memory. |
| `class` vs `struct` vs `record` | class = reference type (heap, identity); struct = value type (copy, small immutable data); record = reference type with value-based equality + `with`; `record struct` = value + equality. DTOs -> records. |
| Value vs reference semantics, boxing | Value types copied on assignment; boxing allocates when a value type becomes `object`/interface - avoid in hot paths. |
| How does the GC work? | Generational (0/1/2, LOH for >= 85 KB), mark-compact, background GC; allocation pressure = pauses; reduce allocations (pooling, `Span<T>`, avoid closures in hot paths); always `Dispose` unmanaged resources (`IDisposable`/`using`). |
| `Task` vs `Thread`; `async` state machine | A Task represents an operation; `await` compiles to a state machine that frees the thread at the await. Threads are expensive and few; tasks are cheap and many. |
| `Task.Run` in ASP.NET? | Rarely: it just moves CPU work to another pool thread (no gain, adds overhead). Use real async I/O; offload long CPU work to a background worker/queue. |
| `lock`, `SemaphoreSlim`, `ConcurrentDictionary` | `lock` can't contain `await`; use `SemaphoreSlim.WaitAsync` for async mutual exclusion; `ConcurrentDictionary.GetOrAdd` may run the factory more than once (use `Lazy<T>`). |
| DI lifetimes & captive dependency | See `02-dotnet-concepts.md` §1. |
| Middleware vs filter vs attribute | See §4. |
| `IOptions` vs `IOptionsSnapshot` vs `IOptionsMonitor` | Singleton snapshot at startup / per-request reload / live change notification. |
| Abstract class vs interface | Interface = contract (multiple); abstract class = shared state/behaviour + one base. Since C# 8, interfaces can have default members - still prefer composition. |
| SOLID with an example | S: `Order` enforces order rules only. O: new gateway = new `IPaymentGateway`, no edits to `OrderService`. L: `FakePaymentGateway` substitutes the real one. I: small interfaces (`IRealtimeNotifier`). D: Application depends on abstractions, Infrastructure implements them. |
| Delegates, events, `Func`/`Action`, LINQ deferred execution | Delegates are typed function references; LINQ queries execute when enumerated; multiple enumeration = multiple executions (materialise with `ToList` when needed). |
| `ConfigureAwait(false)` | Skips resuming on the captured context. Libraries: yes. ASP.NET Core app code: no context, so no effect; UI apps: matters. |
| Exceptions: `throw` vs `throw ex` | `throw;` preserves the stack trace; `throw ex;` resets it. Don't catch `Exception` to swallow; catch what you can handle. |
| gRPC vs REST vs GraphQL | REST: ubiquitous, cacheable, browser-friendly. gRPC: binary/HTTP2, strong contracts, great service-to-service, poor in browsers. GraphQL: flexible client-driven queries, solves over/under-fetching, adds N+1/caching/complexity concerns. |