# System design - from ShopFlow to the whole-board questions

The kit: *"System Design questions decide mid and senior offers. Start once you have shipped one real full stack app."* ShopFlow is that app. This doc
(1) grows it from **one server to a global-scale shape**, then (2) answers the **classic interview designs** using the same building blocks.

## 0. A method that works in the room (say it out loud)

1. **Clarify requirements** - functional (what), non-functional (latency, availability, consistency, scale, cost, compliance). Ask for numbers; if none, state assumptions.
2. **Estimate** - QPS (average and peak), storage/year, bandwidth. Order-of-magnitude is enough; it decides single node vs sharded.
3. **API + data model** - the 3-5 endpoints and the core entities with keys and access patterns.
4. **High-level design** - boxes and arrows: client -> LB -> stateless services -> cache -> DB -> queue -> workers.
5. **Deep-dive the 1-2 hardest parts** (consistency, hot keys, fan-out, double booking).
6. **Bottlenecks and failures** - what breaks first at 10x? What is the blast radius? How do we know (metrics)?
7. **State trade-offs explicitly** - every choice has a cost. Interviewers grade the reasoning, not the logo set.

**Back-of-envelope cheat-sheet:** 1 day ~ 86,400 s ~ 10^5 s. 1M req/day ~ 12 req/s. 100M/day ~ 1.2k/s. Peak ~ 2-10x average.
Read from memory ~100 ns, SSD ~100 us, network round trip in a data centre ~0.5 ms, cross-continent ~100-150 ms. One modern DB node: thousands of simple queries/s; one app instance: hundreds-thousands of req/s.

## 1. Scaling ShopFlow step by step

| Stage | Load | What you do | What it exposes in the code |
|---|---|---|---|
| 1. Single box | 10 rps | App + SQL on one VM. Ship it. | - |
| 2. Split tiers | 100 rps | Managed SQL (backups, patching) separate from app | Connection string + firewall; `/health/ready` |
| 3. **Scale out the app** | 1k rps | 3+ instances behind a **load balancer**, autoscale on CPU/latency | **Statelessness** - see below |
| 4. Cache | 5k rps | Shared cache (Redis) for hot reads; CDN for static files + public catalogue | `CatalogCache` is per-instance: invalidation does not cross servers |
| 5. **Offload work** | any | Queue + workers for e-mail, webhooks, reports | Outbox -> real broker; worker scales independently |
| 6. **DB relief** | 10k+ rps | Read replicas for reports/search; indexes; archive old orders; partition big tables | Route read-only queries (`ReportService`) to a replica; accept replica lag |
| 7. Shard / specialise | 100k+ rps | Shard by `TenantId`; search engine for full-text; separate analytics store | `TenantId` is already on every row = natural shard key |
| 8. Multi-region | global | Active-passive (DR) or active-active with regional data residency | Conflict resolution, global DNS/Front Door |

### Statelessness - the property that makes stage 3 possible

*"Your app works on one server. Now run five."* Anything remembered in one process's RAM breaks when the next request lands elsewhere. Audit of ShopFlow:

| State | Where it is | Safe to scale out? |
|---|---|---|
| Identity | **JWT** (self-contained, verified with a shared key) | Yes - no server session |
| Refresh tokens, idempotency keys, processed webhook ids | **SQL** | Yes - shared |
| Cart | Browser `localStorage` | Yes |
| Product/report cache | `IMemoryCache` in each instance | **Works, but stale per instance** -> move to Redis |
| SignalR connections | In-process | **No** - a message produced on instance A can't reach a user connected to B -> add a **backplane** (Redis / Azure SignalR Service) |
| Rate-limit counters | In-process | **Partially** - each instance limits separately; distributed limit needs Redis/gateway |
| Outbox worker | Runs in every instance | Yes - `UPDLOCK, READPAST` lets them cooperate without double-processing |

**Sticky sessions** (LB pins a user to a server) are a crutch that hides state problems and breaks autoscaling/rolling deploys. SignalR's WebSocket naturally sticks for the life of the connection; reconnects can land anywhere - hence the backplane.

### Load balancing
L4 (TCP) is fast and dumb; **L7 (HTTP)** can route by path/host, terminate TLS, do health checks, WAF, rate limiting. Algorithms: round-robin, least-connections, consistent hashing (for caches). Health checks use **readiness** (`/health/ready`): an instance whose DB is unreachable is removed from rotation instead of serving 500s. Rolling deploys drain connections first.

## 2. Caching - and "what could go wrong" (the kit's follow-up)

| Pattern | How | Used here |
|---|---|---|
| **Cache-aside** | App checks cache, on miss loads DB and fills cache | `CatalogCache.GetOrCreateAsync` |
| Write-through / write-behind | Write goes through the cache / is buffered | Not used (complexity, durability risk) |
| HTTP caching | `Cache-Control`, `ETag` / `If-None-Match` (304) | Static assets (nginx, 1 year, hashed names); API sends `no-store` (authenticated data) |
| CDN | Edge cache near users | Static bundle; public catalogue pages if SSR'd |

Failure modes and the fix:

1. **Stale data** - inevitable; choose a TTL consistent with the business ("price may be 30 s stale; stock is re-checked in SQL at purchase anyway" - the cache never decides money).
2. **Invalidation** (the hard problem) - here a per-tenant change token evicts all pages on any write; with several instances you need pub/sub invalidation or short TTLs.
3. **Cache stampede / thundering herd** - a hot key expires and 1,000 requests hit the DB together. Fixes: *request coalescing* (one loader, others await it - `HybridCache`/`Lazy<Task>`), *jittered TTLs*, *stale-while-revalidate*, pre-warm.
4. **Cache penetration** - requests for keys that never exist bypass the cache. Cache negative results briefly; validate ids first.
5. **Hot key** - one key saturates one cache shard; replicate it or add a local L1 cache in front.
6. **Cold start** - after a deploy/failover the cache is empty and the DB takes full load; warm gradually, keep the DB able to survive the miss rate.
7. **Never cache per-user data under a shared key** (the key contains `TenantId` here; user-specific data would need `UserId` too).

## 3. CAP, consistency and what ShopFlow chooses

*CAP:* during a **network partition** you must choose **C**onsistency (refuse/err some requests) or **A**vailability (answer with possibly stale data). No partition -> you can have both; the everyday trade is **latency vs consistency** (PACELC).

| Data | Choice | Why |
|---|---|---|
| Stock, order status, payments | **Strong consistency** (single SQL primary, ACID) | Wrong numbers cost money - prefer an error ("sold out", 409) to a lie |
| Product list cache, report, search | **Eventual consistency** | A stale price for 30 s is acceptable; correctness is re-checked at write time |
| Real-time notifications | **At-least-once, best-effort timing** | The source of truth is the DB; the UI refetches the order |
| Cross-service flows (if we split) | **Sagas** + compensation (`Cancel` restores stock, `RefundRequired`) | Distributed transactions (2PC) don't scale; compensate instead |

## 4. Queues, spikes and "10x traffic on checkout"

Spike playbook, in the order you would actually act:

1. **Absorb**: queue-based **load levelling** - accept the order request fast, process steadily. (ShopFlow's side effects already go through the outbox.)
2. **Scale**: autoscale stateless app instances (on request rate/latency, not just CPU); pre-scale before a known sale.
3. **Protect the DB**: connection-pool caps, read from cache, avoid N+1, shed expensive endpoints first.
4. **Rate limit and shed**: per-user/IP limits (`429` + `Retry-After`); under overload reject low-value traffic (search, recommendations) to keep **checkout** alive (*graceful degradation*, *load shedding*).
5. **Hot row problem**: a flash sale on one SKU serialises on that row's lock. Options: **reservation tokens / sharded stock counters** (e.g. split 1,000 units into 10 buckets of 100 and pick one), a **virtual waiting room** (queue users and admit at a safe rate), or async order acceptance (return `202 Accepted`, confirm by notification).
6. **Idempotency** everywhere so retries during the storm don't double-charge.
7. **Observe**: dashboards for p95/p99 latency, error rate, queue depth, DB CPU/locks/blocking, pool wait time.

## 4b. Designing for failure ("the payment provider is down - what does the user see?")

| Tool | Purpose | In ShopFlow |
|---|---|---|
| **Timeouts** on every outbound call | Never wait forever (a stuck call holds a thread + connection) | Resilience handler total/attempt timeouts |
| **Retries with exponential backoff + jitter** | Heal transient faults without synchronised retry storms | Same handler; EF `EnableRetryOnFailure` |
| **Circuit breaker** | Stop calling a failing dependency; fail fast; probe to recover | Same handler -> `BrokenCircuitException` -> `503 + Retry-After` |
| **Bulkheads** | Isolate resources so one slow dependency can't starve everything | Separate `HttpClient` per dependency; rate limiter partitions |
| **Idempotency** | Make retries safe | `Idempotency-Key`, webhook dedupe table |
| **Fallbacks / degradation** | Do something useful when a part is down | Order stays **Pending**; user can pay later; catalogue keeps working |
| **Compensation** | Undo earlier steps when a later one fails | Cancel returns stock; `RefundRequired` |

Answer to the question: *the order is already saved as Pending; the pay button gets a fast `503` with a retry hint (circuit open), not a 30 s hang; nothing is charged twice because the provider call carries an idempotency key; a background job or the user can retry later.*

## 5. Observability

* **Logs** (structured, correlation id in every line - `CorrelationIdMiddleware` + Serilog), **metrics** (counters/histograms), **traces** (one request across services - OpenTelemetry).
* **Four golden signals:** latency, traffic, errors, saturation. Alert on **symptoms users feel** (error-rate/latency SLO burn), not on every CPU blip.
* **SLO example:** 99.9% of checkout requests succeed under 800 ms over 30 days -> error budget ~43 min/month; spend it on releases.
* **"A user reports an error you can't reproduce."** Get the correlation id (shown in the toast), find the request, read its log lines and exception, check its query plan / downstream call, then reproduce locally with that data shape.
* **Bad deploy, first five minutes:** *mitigate first* (roll back / disable the feature flag), then diagnose; communicate; keep the broken build artefact for analysis.
* **Health:** liveness vs readiness (see `02-dotnet-concepts.md`).

## 6. Multi-tenancy
Tenant isolation options and their trade-offs: `04-sql-concepts.md` §7. Also design for: **noisy neighbours** (per-tenant rate limits/quotas), **per-tenant config & feature flags**, **tenant-aware caching keys**, **per-tenant data export/delete** (GDPR), **tenant in every log line**.

## 7. Mapping to a cloud (Azure example; same shape on AWS/GCP)

| ShopFlow part | Azure service | Notes |
|---|---|---|
| SPA + nginx | Static Web Apps / Storage + Front Door (CDN, WAF) | Cache hashed assets for a year |
| API | App Service / Container Apps / AKS | Autoscale rules; slots for blue/green |
| SQL Server | **Azure SQL Database** | Built-in backups + point-in-time restore, read replicas, elastic pools for tenants |
| Cache / SignalR backplane | Azure Cache for Redis / Azure SignalR Service | |
| Outbox -> broker | Azure Service Bus (queues/topics) | Dead-letter queue for poison messages |
| Secrets | Key Vault + managed identity | No secrets in env files |
| Logs/metrics/traces | Application Insights / OpenTelemetry | |
| CI/CD | GitHub Actions -> container registry -> deploy | Image tagged by commit SHA |

---

# Classic interview designs (use the same blocks)

## A. URL shortener ("the most common warm-up. Know it cold.")

**Requirements:** create short link, redirect, optional custom alias/expiry, click analytics. 99.99% available redirects, low latency. **Assume** 100 M new links/month, read:write = 100:1.

**Estimate:** writes ~40/s; reads ~4,000/s avg, ~40k/s peak. Storage: ~500 B x 100 M x 12 x 5 yrs ~ **3 TB** (fits one sharded SQL/KV store). Code length: base62 ^ 7 = 3.5 trillion combinations - plenty.

**API:** `POST /links {url, alias?, expiresAt?} -> {code}`; `GET /{code} -> 302 Location: <url>`; `GET /links/{code}/stats`.

**Data model:** `Links(Code PK, LongUrl, UserId, CreatedAt, ExpiresAt)`; clicks go to an append-only `Clicks` store (or a stream), **never** updated on the redirect path.

**Generating codes (deep dive):**
1. *Counter -> base62*: a global counter encoded in base62. Unique, no collisions, but guessable and the counter is a hotspot -> allocate **ranges** (each app instance reserves 10,000 ids at a time).
2. *Hash (MD5/SHA of URL) -> first 7 chars*: needs collision handling (retry with salt) and same URL maps to same code.
3. *Random 7 chars + unique constraint + retry on conflict*: simplest, non-guessable. Collision probability is tiny until the keyspace is mostly full.

**Redirect path (the hot path):** `CDN/edge -> LB -> app -> Redis (code -> url) -> DB on miss`. ~95%+ cache hit; popular links stay hot (LRU). **301 vs 302:** 301 is cacheable by browsers (fewer hits, but you lose analytics and can't change the target); **302** keeps every click visible. Analytics: publish a click event to a queue/stream asynchronously; aggregate in a worker.
**Scale/failure:** stateless app, Redis replicas, DB sharded by `hash(Code)`; rate-limit link creation per user/IP (abuse/spam); expired-link cleanup job; block malicious URLs (safe-browsing check).

## B. Social media feed

**Core tension: fan-out on write vs on read.**
* *Fan-out on write (push):* when A posts, write the post id into every follower's precomputed timeline (Redis list / table). Reads are O(1) and fast. **Problem:** a celebrity with 50 M followers = 50 M writes per post.
* *Fan-out on read (pull):* build the timeline at read time by merging recent posts of everyone you follow. Cheap writes, **slow reads**.
* **Hybrid (real answer):** push for normal users; for celebrities **don't** fan out - at read time merge your pushed timeline with the recent posts of the few celebrities you follow.

Other pieces: **cursor pagination** (`before=postId/timestamp` - offsets break as new posts arrive: see `Cursor.cs`), timeline cache with TTL, media via object storage + CDN, ranking service (reads features, scores, can be async), the follow graph in a graph-friendly store or an adjacency table sharded by user id, counters (likes) via approximate/async aggregation, eventual consistency is fine ("my post appears for followers within seconds").

## C. Booking system with no double bookings ("same technique as our stock")

Resource (room/seat/slot) booked for a time range; two users click at the same instant.

1. **Let the database arbitrate** - never "check availability in app code then insert". Model each bookable unit as a row, e.g. `Slot(ResourceId, StartsAt, Status)` with `UNIQUE (ResourceId, StartsAt)` on `Booking`, and take it with an **atomic conditional write**: `UPDATE Slot SET Status='Held', HeldBy=@u, HeldUntil=@t WHERE Id=@id AND Status='Free'` -> rows affected = 1 means you got it. (Same shape as `Stock >= q`.) For ranges (a hotel 3-night stay) use one row per night inside one transaction, or a range-overlap exclusion constraint where the engine supports it (PostgreSQL `EXCLUDE USING gist`).
2. **Hold, then confirm** - a *temporary reservation with TTL* (5-10 minutes) so payment can complete; a sweeper job (or `HeldUntil < now` check on read) releases expired holds.
3. **Idempotency key** on the booking request; **outbox** for confirmation e-mails.
4. **Prove it** with a concurrency test (N parallel requests for one slot -> exactly one succeeds) - see `Forty_buyers_race...`.
5. Scale: shard by resource id/tenant; cache availability *read-only* (the write path always re-checks in the DB); overbooking is a business policy decision (airlines do it deliberately).

## D. Rate limiter (small but common)
Algorithms: **fixed window** (simple, burst at boundaries), **sliding window**, **token bucket** (allows bursts, smooth refill - used here per user), **leaky bucket** (smooth output). Distributed: counters in Redis with atomic `INCR`+`EXPIRE` or a Lua script; return `429` + `Retry-After`; key by user id when authenticated, else IP; different budgets per endpoint (`/auth` strict).

## E. Notification system
Event -> **queue** -> per-channel workers (e-mail/SMS/push) with **retries + dead-letter queue**, **idempotency** (don't send twice), user preferences, templating, rate limits per provider, delivery receipts via webhooks. ShopFlow's outbox + dispatcher is this in miniature.

## F. Chat / presence
WebSocket gateway tier (sticky by connection), message persistence (write to DB/stream first, then fan out), per-conversation ordering (partition by conversation id), presence via heartbeats + TTL in Redis, offline delivery via push notifications, read receipts as events, backplane/pub-sub to route between gateway nodes.

## G. "Should we split into microservices?" (usually: no)
Say: *not yet.* Microservices trade **in-process calls and ACID transactions** for **network calls, eventual consistency, distributed tracing, versioned contracts and a platform team**. Split when: independent scaling/deploy cadence is a real, measured need; separate teams own separate domains; a component has different availability/tech requirements. Prepare for it by keeping module boundaries clean (as the project layout does) and communicating through events (the outbox) - then a module can be carved out later with its own database.