# SQL Server concepts a senior must own

The kit says SQL is "frequently its own interview round" and "the most underrated skill". Everything below is runnable:
`sql/00_load_sample_data.sql` -> 1,000 customers, 100,000 orders, ~200,000 order lines. Then run files `01` ... `05` with
`sqlcmd -S "(localdb)\MSSQLLocalDB" -d ShopFlow -E -i sql\0X_*.sql` (or paste into SSMS / Azure Data Studio and turn on *Include Actual Execution Plan*).

> The numbers in this document are **measured on this machine with that data set** (logical reads = 8 KB pages touched; lower is better).

## 1. Schema design & normalisation

* **1NF** atomic columns, no repeating groups (items are rows in `OrderItems`, not `Item1, Item2, Item3` columns).
* **2NF** no partial dependency on part of a composite key. **3NF** no transitive dependency (category *name* lives in `Categories`, not repeated on every product).
* **Denormalise only on purpose and say why.** `OrderItems.ProductName/UnitPrice` are *snapshots* - price history is a business fact, not a derived value.
  `OrderItems.TenantId` is denormalised to make tenant isolation (filters / RLS) cheap on child rows. Reports could also cache aggregates; that is a deliberate, measured trade of write cost for read speed.
* **Keys:** surrogate GUID PK + *natural* unique keys enforced separately (`UNIQUE (TenantId, Sku)`, `UNIQUE Email`). The unique index - not an `if (exists)` in C# - is what really prevents duplicates under concurrency.
* **Constraints are the last line of defence:** `CHECK (Stock >= 0)`, FKs with `ON DELETE RESTRICT` (you cannot delete a product that was sold), `NOT NULL`.
* **Data types:** `decimal(18,2)` for money (never `float`), `datetime2` (UTC) over `datetime`, `nvarchar` for user text, `rowversion` for concurrency, `uniqueidentifier` for ids.
* **Why not MongoDB here?** Orders, stock and payments need multi-row **ACID transactions**, strict relationships and ad-hoc reporting (joins, window functions). A document store shines for flexible, hierarchical, read-mostly data with a single access pattern (catalogue content, activity feeds, event logs). The honest senior answer is *"relational first; add a document/search store when a specific access pattern demands it"* - and then naming that pattern.

## 2. Querying (file `01_window_functions_and_joins.sql`)

| Interview question | Answer pattern |
|---|---|
| **Top 3 orders per customer** | `ROW_NUMBER() OVER (PARTITION BY UserId ORDER BY Total DESC, Id)` in a CTE, filter `rn <= 3` outside (window functions can't be in `WHERE`). Add a tie-breaker for determinism. |
| `ROW_NUMBER` vs `RANK` vs `DENSE_RANK` | Values 100,100,90 -> 1,2,3 / 1,1,3 / 1,1,2. The file prints exactly this. |
| Running total, share of total | `SUM(x) OVER (ORDER BY day ROWS UNBOUNDED PRECEDING)`; `SUM(x) OVER ()` for the grand total. |
| Gap since the previous row | `LAG(CreatedAt) OVER (PARTITION BY UserId ORDER BY CreatedAt)`. |
| Customers with no orders | `NOT EXISTS` - **not `NOT IN`**, which returns *nothing* if the subquery yields a single `NULL`. |
| `WHERE` vs `HAVING` | `WHERE` filters rows *before* grouping (can use indexes); `HAVING` filters groups *after* aggregation. |
| `INNER/LEFT/RIGHT/FULL/CROSS JOIN`, `APPLY` | `LEFT JOIN ... WHERE right.id IS NULL` = anti-join. `CROSS APPLY` = per-row table function / "top-N per row" (used in the data loader). |
| Delete duplicates keep newest | `ROW_NUMBER()` partitioned by the natural key, `DELETE` the CTE where `rn > 1`. |
| `UNION` vs `UNION ALL` | `UNION` de-duplicates (sorts/hashes - slower); use `ALL` unless duplicates are wrong. |
| `COUNT(*)` vs `COUNT(col)` | `COUNT(col)` skips NULLs. |
| NULL logic | `NULL = NULL` is unknown; use `IS NULL`; `ISNULL/COALESCE`; aggregates ignore NULL. |

The production report endpoint (`Application/Reports/ReportService.cs`) is Q"top N per group" written as raw parameterised SQL.
**Gotcha:** EF *global query filters do not apply to raw SQL* - the tenant predicate (`o.TenantId = {tenant}`) is written by hand, and a test proves no cross-tenant leakage.

## 3. Indexes & execution plans (file `02_indexes_and_query_plans.sql`)

**Mental model:** a clustered index *is* the table (B-tree ordered by the key, leaf = the data). A non-clustered index is a separate B-tree whose leaf holds the key columns + a pointer back (the clustering key). A query can **seek** (navigate the tree to a range: O(log n)) or **scan** (read everything: O(n)).

### Measured results (100,000 orders)

| Demo | Without | With | Lesson |
|---|---|---|---|
| Admin "all orders" first page (`ORDER BY CreatedAt DESC` for a tenant) | **961** reads (scan + sort) | **~3** reads (index seek, no sort) | An index must match `WHERE` equality columns *then* the `ORDER BY`. Found missing by this lab -> migration `AddOrdersTenantCreatedIndex`. |
| Page 4000 with **OFFSET 80000** | **461** reads | - | `OFFSET n` reads and discards n rows. Cost grows with page number. |
| Same rows with **keyset** (`WHERE (CreatedAt,Id) < cursor`) | - | **6** reads | Cost is constant at any depth. This is what `Cursor.cs` + `ProductService` do. |
| `YEAR(CreatedAt)=2026 AND MONTH(...)=8` | **575** reads | **229** reads for the equivalent range `CreatedAt >= '2026-08-01' AND < '2026-09-01'` | **Sargability:** don't wrap the indexed column in a function; compare the raw column to a computed range. |
| `SELECT Id,Total,CreatedAt WHERE Status=0` (10k rows) | **1,362** reads | **76** reads with `INCLUDE (Total, CreatedAt)` | **Covering index** removes the key lookup (a random read per row). Then it is dropped again: every index taxes every INSERT/UPDATE. |
| "My newest 20 orders" | - | 72 reads: seek + 20 key lookups, **no Sort operator** | Composite `(TenantId, UserId, CreatedAt, Id)` serves equality + order. |

### Rules to recite

1. **Equality columns first, then range/sort columns** in a composite index. Leftmost-prefix: `(A,B,C)` serves `A`, `A+B`, `A+B+C`; not `B` alone.
2. **Selectivity matters:** an index on a column with 3 distinct values rarely helps alone; combine it with a selective column.
3. **Key lookup** = seek on a non-clustered index, then fetch missing columns from the clustered index. Fix with `INCLUDE`, or accept it for few rows.
4. **Sargable predicates:** `col = @x`, ranges, `LIKE 'abc%'`. Not sargable: `FUNC(col) = @x`, `col LIKE '%abc'`, implicit conversions (`varchar` column vs `nvarchar` parameter converts the *column*).
5. **Foreign keys are not auto-indexed in SQL Server.** Index them (`OrderItems.ProductId`, `OrderId`) or joins and cascade checks scan.
6. **Filtered index:** `WHERE ProcessedAt IS NULL` on the outbox keeps the index tiny however large the table grows (`AppDbContext`).
7. **Statistics** drive plan choice; stale stats = bad plans. `UPDATE STATISTICS` after bulk loads (the loader does).
8. **Parameter sniffing:** the plan is compiled for the *first* parameter values; a plan great for a rare value can be awful for a common one. Cures in order: fresh stats -> `OPTION (RECOMPILE)` -> `OPTIMIZE FOR` -> restructure.
9. **Reading a plan:** fat arrows = many rows; *Seek vs Scan*; *Key Lookup*; *Sort* / *Spill to tempdb* warnings; estimated vs actual rows far apart = bad statistics; yellow triangle warnings (implicit conversion, missing index).
10. **More indexes != faster.** Each costs space and write time. Query `sys.dm_db_index_usage_stats` for indexes nobody reads (script section 6); `sys.dm_db_missing_index_*` for suggestions (section 5) - review, don't blindly create.

## 4. Transactions, locking, isolation (file `03_transactions_locking_isolation.sql`)

* **ACID** - see the file for a one-line mapping onto this app (order + stock + outbox + idempotency record commit together).
* **"Two users buy the last item."** Naive read-modify-write **oversells** (both read `Stock=1`). Fixes, from best to most situational:
  1. **Atomic conditional update** `UPDATE ... SET Stock = Stock - @q WHERE Id=@id AND Stock >= @q` and check rows affected. One statement, no explicit lock reasoning. *(used)*
  2. **Pessimistic lock:** `SELECT ... WITH (UPDLOCK, ROWLOCK)` then update in the same transaction.
  3. **Optimistic:** `rowversion` compare; loser retries or gets 409. *(used for admin edits)*
  4. `SERIALIZABLE`/`SNAPSHOT` isolation - heavier, deadlock/conflict-prone.
  Proof: integration test **`Forty_buyers_race_for_ten_items...`** - exactly 10 succeed, 30 get 409, stock ends at exactly 0.
* **Isolation levels:** table in the file. Production default advice: **`READ_COMMITTED_SNAPSHOT ON`** (readers don't block writers) + atomic statements + rowversion. `NOLOCK` (= READ UNCOMMITTED) can return rolled-back and duplicated/missing rows: not for money.
* **Locks:** shared (S) for reads, update (U), exclusive (X) for writes; granularity row -> page -> table (lock escalation at ~5,000 locks); intent locks signal hierarchy. **Blocking** = waiting for a lock (diagnose with `sys.dm_tran_locks`, `sp_whoisactive`). **Deadlock** = a cycle; SQL Server kills the cheaper transaction (error **1205**).
* **Avoiding deadlocks:** same lock order everywhere (`OrderService` sorts by `ProductId`; test `Orders_touching_the_same_products_in_opposite_order_never_deadlock`), keep transactions short, no user interaction inside a transaction, index well (fewer rows locked), retry on 1205 (`EnableRetryOnFailure`).
* **Never hold a transaction across a network call** to a third party (the payment provider is called *outside* the DB transaction; the webhook comes back later).

## 5. Safe migrations (file `04_safe_migrations.sql`)

*"Add a non-null column to a table with 50 million rows."* **Expand -> migrate -> contract**: add nullable (instant) -> deploy code that writes it -> backfill in **small batches** (short locks, log can truncate, resumable) -> verify no NULLs -> set `NOT NULL` -> later drop the old thing.
Also: every migration must be backward-compatible with the *previous* app version (rolling deploys run both); `CREATE INDEX ... WITH (ONLINE = ON)`; add FKs `WITH NOCHECK` then validate; run migrations as a pipeline step with a tested rollback.

## 6. Connection pooling

* Opening a physical SQL connection is expensive (TCP + TLS + auth). ADO.NET keeps a **pool** per connection string; `DbContext` borrows a connection only while a command runs and returns it on dispose, so *short-lived scoped contexts* are cheap.
* Pool exhaustion (default max 100) shows up as timeouts waiting for a connection: causes are slow queries, leaked connections (not disposing), or too many concurrent requests per instance. **Serverless/many-instance + SQL = classic production failure**: N instances x pool size can exceed the server's connection limit -> cap the pool per instance, use a pooler/proxy, scale the DB tier.
* Our `EnableRetryOnFailure` retries transient faults (failover, deadlock victim, timeout) - which is why user transactions run inside an execution strategy.

## 7. Multi-tenancy at the database level

| Model | Isolation | Cost / ops | Use when |
|---|---|---|---|
| **Shared schema + `TenantId`** *(ShopFlow)* | Logical (app filter + optional RLS) | Cheapest, one migration | Many small tenants |
| Schema per tenant | Better, per-tenant restore is awkward | Migrations x N | Mid-size tenants |
| Database per tenant | Strongest, per-tenant backup/restore, noisy-neighbour control | Highest; elastic pools help | Large/regulated tenants |

**Defence in depth with SQL Server Row-Level Security** (Exercise 7.12): the app sets `SESSION_CONTEXT('TenantId')` per connection and a security policy filters every query *in the engine*, so even raw SQL or a forgotten filter cannot leak:

```sql
CREATE FUNCTION dbo.fn_tenant_predicate(@TenantId uniqueidentifier) RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS ok WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
CREATE SECURITY POLICY dbo.TenantPolicy ADD FILTER PREDICATE dbo.fn_tenant_predicate(TenantId) ON dbo.Orders WITH (STATE = ON);
```

## 8. Full-text search vs LIKE

`LIKE 'abc%'` is index-friendly (used for the product search). `LIKE '%abc%'` always scans. For relevance, stemming, multi-word and typo tolerance use **SQL Server Full-Text Search** (`CONTAINS/FREETEXT`) or a search engine (Elasticsearch/Azure AI Search) when ranking/facets are needed (Exercise 7.13: measure both, as the kit suggests).

## 9. Backup & restore (file `05_backup_restore.sql`)

Recovery model (`SIMPLE` vs `FULL`) decides *what you can restore to*. Chain: full + differential + log backups; **RPO** = log-backup interval, **RTO** = restore time. Always restore **a copy** side by side, extract the damaged rows, reconcile. *"A backup you have never restored is not a backup"* - schedule a real restore + `DBCC CHECKDB` test.

## 10. Quick T-SQL facts worth knowing

* `OUTPUT inserted.*` returns rows changed by DML; `MERGE` has long-standing bugs/concurrency traps - prefer explicit `UPDATE` + `INSERT` in a transaction.
* `TOP (n)` needs `ORDER BY` to be deterministic; `OFFSET/FETCH` is the standard paging syntax.
* Table variables vs temp tables: temp tables have statistics (better plans for many rows); table variables don't (fine for tiny sets).
* CTE = readable subquery (not materialised by default); recursive CTE for hierarchies.
* `SET NOCOUNT ON` in procedures avoids "rows affected" chatter; `TRY...CATCH` + `THROW`; `XACT_ABORT ON` rolls back the whole transaction on error.
* Temporal tables (`SYSTEM_VERSIONING`) give free history/audit; Change Tracking/CDC feed outbox-like pipelines without polling.