# Angular (21) + TypeScript concepts a senior must own

Paths are under `frontend/src/app/`.

## 1. The mental model in five lines

* **Components** are classes + a template. They are **standalone** (no NgModules): each one lists its own `imports`.
* **State** is held in **signals**; **derived state** in `computed`; **side effects** in `effect`.
* **Services** (`providedIn: 'root'`) are singletons obtained with **`inject()`**. Stores like `CartStore`/`AuthStore` are just services holding signals.
* **Async/event streams** (HTTP, typing, WebSockets) are **RxJS Observables**; bridge to signals with `toSignal` / `toObservable`.
* **Change detection** only re-renders what a changed signal touches (zoneless is the default in v21), and every component here is `OnPush`.

## 2. Signals vs RxJS - when to use which (a favourite question)

| Use a **signal** for | Use an **Observable** for |
|---|---|
| Current value of something (cart lines, logged-in user, `loading`) | Things that happen over time (keystrokes, HTTP, WebSocket messages) |
| Synchronous derived values (`total = computed(...)`) | Cancellation, debouncing, retries, combining async sources |
| Template bindings | Operators: `debounceTime`, `switchMap`, `retry`, `shareReplay` |

Bridging in this repo: `product-list.page.ts` turns the filter **signal** into an **Observable** (`toObservable`), runs the RxJS pipeline, and writes results back into **signals**.
`ToastHost`/`CartStore.total` are pure signals. `RealtimeService` exposes an Observable of push events.

## 3. RxJS flattening operators - the interview table

| Operator | Behaviour on a new outer value | Use when | In this repo |
|---|---|---|---|
| `switchMap` | **Cancel** the previous inner request, start the new one | Search-as-you-type, route params - only the latest matters | `product-list.page.ts` (search), `orders.page.ts#pay` (sequential dependent calls) |
| `concatMap` | **Queue** - run one after another, in order | Order matters (updates, saves) | `orders.page.ts` (realtime updates applied in order) |
| `mergeMap` | Run **all in parallel** | Independent work, order irrelevant | - (e.g. fetch 10 thumbnails) |
| `exhaustMap` | **Ignore** new values while one is running | Submit/login button (double-click protection) | Exercise 7.4 replaces the manual `busy()` flag with it |

*Why `switchMap` fixes a real bug:* typing "lap" fires requests for "l", "la", "lap". If the "l" response arrives **last**, a naive implementation shows the wrong list.
`switchMap` cancels the older ones, so a stale response can never overwrite a newer one.

*Error handling placement:* `catchError` goes **inside** the `switchMap` so a failed request ends only that inner stream; placed outside, it would terminate the whole search stream for good.

*Leaks:* an unsubscribed long-lived Observable keeps the component alive. Fixes used here: `takeUntilDestroyed()`, `toSignal` (auto-unsubscribes), or the `async` pipe.

**Hand-written debounce / throttle** (the kit's interview question) - closures + timers:

```ts
function debounce<A extends unknown[]>(fn: (...a: A) => void, ms: number) {
  let t: ReturnType<typeof setTimeout> | undefined;
  return (...args: A) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };   // only the LAST call in a burst runs
}
function throttle<A extends unknown[]>(fn: (...a: A) => void, ms: number) {
  let last = 0;
  return (...args: A) => { const now = Date.now(); if (now - last >= ms) { last = now; fn(...args); } };   // at most once per window
}
```
Debounce = "wait until the user stops" (search box). Throttle = "at most every N ms" (scroll/resize handlers).

## 4. Dependency injection in Angular

* `inject(Service)` works in constructors, field initialisers and functional guards/interceptors (an *injection context*).
* **Hierarchical injectors:** root -> route -> component. `providedIn: 'root'` = one instance app-wide.
* **Functional providers** (`provideRouter`, `provideHttpClient(withInterceptors([...]))`) in `app.config.ts` replace NgModules.
* **`provideAppInitializer`** runs before the first render: `AuthStore.restore()` silently logs the user back in from the refresh cookie, so a page reload does not look like a logout.
* **Interceptors order:** first listed is outermost. `errorInterceptor` wraps `authInterceptor`, so it sees an error only *after* the token refresh and replay already failed.

## 5. The auth flow on the client (`core/auth.store.ts`, `core/http.interceptors.ts`)

1. Access token (JWT, 15 min) -> **in memory** (signal). Never `localStorage`: any XSS can read it.
2. Refresh token -> **httpOnly cookie** set by the API; JS cannot read it; `SameSite=Strict`, `Path=/api/v1/auth`.
3. Interceptor adds `Authorization: Bearer` to API calls (not to `/auth/*`).
4. On `401` the interceptor calls `AuthStore.refresh()` **once**. Five parallel 401s share **one** refresh via `shareReplay`, because refresh tokens are *single-use* - five parallel refreshes would make four fail and trip the server's theft detection. The test `auth.interceptor.spec.ts` proves it with `HttpTestingController.expectOne` (it fails if two refreshes happen).
5. If the refresh fails -> session cleared -> redirect to `/login?returnUrl=...`.
6. **Guards are UX, not security.** They hide screens. Real enforcement is the server (JWT validation, role policies, tenant filter, ownership in the query).

## 6. Change detection

* **Default (zone.js):** after *any* async event, Angular checks the whole tree.
* **`OnPush`:** a component is checked only when an `input()` reference changes, an event fires inside it, or a signal it reads changes. Forces **immutable updates** (`items.update(c => [...c, x])`, never `push`).
* **Zoneless (default in v21):** no zone.js patching of every timer/promise; signals tell Angular exactly what to refresh. Smaller bundle, fewer checks.
* Template functions run on every check - use `computed`/pipes, not method calls with work inside.
* `track` in `@for` is **mandatory** and should be a stable id (`track p.id`): Angular moves existing DOM nodes instead of destroying and recreating them (and keeps focus/inputs).

## 7. Forms

* **Typed reactive forms**: `fb.nonNullable.group({...})` -> `form.getRawValue().email` is `string`, not `string | null`. Both login and register pages.
* Client validators are **UX**. The API validates again (FluentValidation) and returns `errors: { field: [messages] }`; `register.page.ts` renders them.
* Template-driven forms are fine for tiny forms; reactive forms scale (dynamic fields, cross-field validators, unit-testable without a template).

## 7b. Component design

* **Smart vs dumb.** `ProductCard` is presentational: `input.required<Product>()`, `output<Product>()`, no services -> trivially testable (`product-card.spec.ts`). Pages (`ProductListPage`) are the "smart" ones that talk to services.
* **Signal inputs** are typed, required-able and readable inside `computed` (`StatusBadge`).
* **Content projection / `@defer`** for heavy widgets (not needed here): `@defer (on viewport) { <app-chart/> }` lazy-loads below-the-fold components.

## 8. Routing & performance

* **Lazy loading:** every page is `loadComponent: () => import(...)`, so the browser downloads a screen's code on first visit. Run `npm run build` and read the per-route chunk list.
* **Guards** (`canActivate`) are functions; they return `true` or a `UrlTree` redirect (`returnUrl` preserved).
* **`withComponentInputBinding()`**: route params arrive as component `input()`s.
* **Core Web Vitals checklist** (the kit's "LCP is 4.5 s - what do you check first?"): 1) is the LCP element an image - preload/`NgOptimizedImage`, correct size, CDN; 2) server response/TTFB; 3) render-blocking CSS/JS and bundle size (`budgets` in `angular.json` fail the build when exceeded); 4) fonts; 5) layout shift - reserve space; 6) INP - long tasks on the main thread, avoid heavy work in handlers.
* **Large lists:** cursor paging + "Load more" (this repo), or CDK virtual scroll for 10k+ rows.
* **Debounce + cancel** user-driven requests (section 3).

## 9. Security on the client

* **XSS.** Angular escapes `{{ }}` and sanitises `[innerHTML]`/URLs. The danger is *opting out*: `bypassSecurityTrustHtml`, `innerHTML` on `ElementRef`, building HTML strings. This repo never does (grep it).
  Where Angular does **not** help: `href="javascript:..."` bindings you bypass, third-party libs that write the DOM, and server-rendered unsanitised HTML.
* **CSP** (set by the server/nginx) is the second wall: even if markup is injected, inline scripts cannot run.
* **CSRF.** Because the API authenticates with a **header** token (not an ambient cookie), a forged cross-site request has no credential. The only cookie (refresh) is `SameSite=Strict` and path-scoped.
* **Secrets.** Everything in the bundle is public. Never put keys in `environment.ts`; only public identifiers.
* **Dependencies.** `npm ci` (lock file), `npm audit` in CI.

## 10. Rendering strategies (kit: "CSR, SSR, SSG or ISR for this page? Justify it.")

| Strategy | HTML built | Good for | Cost |
|---|---|---|---|
| **CSR** (what ShopFlow does) | In the browser | Logged-in apps where SEO is irrelevant | Blank first paint until JS runs |
| **SSR** | On the server per request | SEO + fast first paint on dynamic public pages | Server cost, hydration complexity |
| **SSG** | At build time | Marketing/docs/blog | Rebuild to change |
| **ISR / revalidate** | SSG + background refresh | Catalogue pages that change hourly | Staleness window |
| **Hydration** | Reuses server HTML, attaches listeners | Avoids re-render flash after SSR | Mismatch bugs if server/client output differ |

*Defence:* ShopFlow's pages all sit behind login -> **CSR** is right. If we added a public storefront for SEO, we would SSR/prerender those routes only (Exercise 7.19).

## 11. TypeScript essentials (all visible in `core/models.ts`)

* **Union literal types** (`type OrderStatus = 'Pending' | 'Paid' | ...`) give exhaustive `switch`es and autocomplete; stronger than enums for JSON.
* **`interface` vs `type`:** interfaces merge/extend (object shapes, public APIs); `type` handles unions, tuples, mapped/conditional types. For plain data they are interchangeable - be consistent.
* **Generics:** `CursorPage<T>` is written once for products and orders. A generic must be *constrained* when it uses members: `<T extends { id: string }>`.
* **Narrowing:** `if (err instanceof HttpErrorResponse)` in `problemOf` - an `unknown` caught value becomes safely typed. Prefer `unknown` over `any`.
* **Utility types:** `Pick<Product,'id'|'name'|'price'>` in `CartStore.add`; `Partial`, `Omit`, `Record`, `ReturnType`, `Parameters` (used in the debounce above).
* **Typing a nullable API field safely:** model it honestly (`paidAt: string | null`) and let the compiler force the null check at each use - do not cast.
* **Strictness:** `tsconfig` has `strict` (+ strict templates): templates are type-checked too.

## 12. JavaScript runtime you must be able to explain

**Event loop.** Order of: synchronous code -> **microtasks** (promise callbacks, `queueMicrotask`, `await` continuations) -> **macrotask** (one timer/I/O callback) -> repeat.

```js
console.log('1');
setTimeout(() => console.log('2'), 0);
Promise.resolve().then(() => console.log('3'));
queueMicrotask(() => console.log('4'));
(async () => { console.log('5'); await null; console.log('6'); })();
console.log('7');
// Output: 1 5 7 3 4 6 2
//   sync: 1, (timer scheduled), (microtasks queued: 3, 4), async body runs synchronously until await -> 5, its continuation queued as microtask (6), then 7.
//   all microtasks drain (3, 4, 6) BEFORE the timer macrotask (2).
```

**`this`:** depends on *how* a function is called. Arrow functions capture `this` lexically - that is why callbacks inside Angular classes use arrows (or `inject()`/signals and avoid `this`-juggling altogether).
**Closures:** `ToastService.show` captures `id` for the delayed `dismiss`; `debounce` above keeps `t` private between calls.
**`==` vs `===`:** `==` coerces (`0 == '' // true`, `null == undefined // true`); always `===`.
**`var/let/const`:** `var` is function-scoped + hoisted (initialised `undefined`); `let/const` are block-scoped with a temporal dead zone.
**Deep clone edge cases:** `structuredClone` handles Dates, Maps, Sets, cycles; `JSON.parse(JSON.stringify())` drops `undefined`/functions, breaks Dates and throws on cycles.
**Event delegation:** one listener on a parent using `event.target` instead of N child listeners (events bubble) - cheaper, and works for rows added later. Angular's own event handling does this for you.

## 13. Testing (Vitest + TestBed)

* Run: `npm test` (Angular 21 uses Vitest + jsdom).
* **Services/stores:** instantiate with `TestBed.inject` - `cart.store.spec.ts`.
* **Components:** `TestBed.createComponent` + `fixture.componentRef.setInput('product', ...)` - `product-card.spec.ts`.
* **HTTP:** `provideHttpClientTesting()` + `HttpTestingController` verifies *exactly which requests happened* - the single-flight refresh test is the best example of testing a concurrency property of the UI.
* Test **behaviour users see** (a disabled button, an emitted event), not private fields.
* E2E (Playwright) is Exercise 7.16.