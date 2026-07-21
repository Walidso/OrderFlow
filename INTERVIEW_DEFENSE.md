# Interview Defense — OrderFlow

Every question an interviewer might ask about this project, with a ~30-second answer **and** the likely follow-up. Practice saying these out loud.

---

### 1. Why Clean Architecture? Isn't it overkill for two services?

**Answer:** It probably *is* more ceremony than this size strictly needs — and I'd say that openly. I chose it to practice the discipline: dependencies point inward, so my business logic in Application has zero references to EF Core, ASP.NET, or RabbitMQ. The payoff is concrete: my unit tests construct handlers directly and swap the database and broker for fakes, no HTTP involved.

**Follow-up: "When would you NOT use it?"** For a small CRUD service or an internal tool, a single project with minimal APIs is faster to build and easier to read. Architecture should match the lifespan and complexity of the system, not a checklist.

### 2. Explain the dependency rule in your solution.

**Answer:** Domain references nothing. Application references only Domain. Infrastructure and Api reference Application. So the core never knows how it's stored or served. Application defines interfaces like `IApplicationDbContext` and `IEventPublisher`; Infrastructure implements them; DI wires them up at startup. That's dependency inversion in practice.

**Follow-up: "How does Application save data without knowing EF?"** Through `IApplicationDbContext` — it exposes `DbSet`s and `SaveChangesAsync`. It's a pragmatic abstraction: it leaks that we use *an* EF-shaped context, but it removes the concrete provider, which is what tests need.

### 3. What is CQRS and why did you use it?

**Answer:** Splitting reads (queries) from writes (commands) into separate request/handler pairs. Here it's "lightweight CQRS" — same database for both sides, no event sourcing. The value: every use case is one small class with one job, so `CreateOrderCommandHandler` has a focused unit test, and cross-cutting concerns like validation plug in as MediatR pipeline behaviors instead of being repeated per handler.

**Follow-up: "What's full CQRS?"** Separate read and write stores, often with events synchronizing them — powerful for read-heavy systems, but eventual consistency between your own stores is real operational pain. I'd need a measured reason to go there.

### 4. Why MediatR instead of calling services directly?

**Answer:** Two things. Decoupling: controllers depend on `ISender`, not on twelve handler classes. And the pipeline: my `ValidationBehavior` runs FluentValidation for *every* command automatically — validation can't be forgotten on a new endpoint. The cost is indirection; you can't F12 from controller to handler.

**Follow-up: "Isn't that just a service locator?"** No — requests are strongly typed and there's exactly one handler per request, checked at startup. It's the mediator pattern with compile-time-known contracts.

### 5. Why RabbitMQ here when you have Azure Service Bus experience?

**Answer:** Requirement was "runs 100% locally with docker compose", and RabbitMQ is free and starts in seconds. Since I used MassTransit, the concepts transfer directly: a MassTransit publish creates an exchange that fans out like an ASB topic with subscriptions, retry policy replaces delivery counts, and the `_error` queue plays the dead-letter role. In my previous project I handled ASB dead-letters and manual message completion, so I was deliberately mapping known concepts to a new broker.

**Follow-up: "Key difference between them?"** ASB has broker-native dead-lettering and sessions; RabbitMQ delegates more to the client — MassTransit *emulates* DLQ by moving poison messages to `<queue>_error`. Same philosophy, different responsibility split.

### 6. Walk me through what happens when a user posts an order.

**Answer:** Controller reads the user id from the JWT claims, builds `CreateOrderCommand`, sends it through MediatR. The validation behavior runs the FluentValidation rules; failures short-circuit into a 400 via the error middleware. The handler creates the `Order` aggregate (status `Pending`), saves it, publishes `OrderCreated`, and the API returns 201 immediately. Inventory consumes the event, tries an all-or-nothing stock reservation, publishes `StockReserved` or `StockRejected`, and a consumer back in the Order service flips the status. The client polls `GET /orders/{id}` to see the outcome.

**Follow-up: "Why not wait for inventory before responding?"** See #7.

### 7. Why async messaging instead of a direct HTTP call to Inventory?

**Answer:** Availability and coupling. With HTTP, if Inventory is down, ordering is down — the services fail together. With events, orders keep flowing; they just sit `Pending` until Inventory recovers and drains the queue. The broker also gives me retry and an error queue for free. The price is eventual consistency: the API answers 201 before the final outcome exists, and the client model must accept "Pending".

**Follow-up: "When is sync the right call?"** When the caller needs the answer *now* to proceed — e.g., payment authorization at checkout. Rule of thumb: queries and must-know-now decisions sync, state-change notifications async.

### 8. What happens if the order saves but publishing the event fails?

**Answer:** This used to be a real gap — save and publish were two separate operations, and a crash between them left a `Pending` order no one would ever process. I closed it with the Transactional Outbox pattern: `CreateOrderCommandHandler` writes the order **and** an `OutboxMessage` row (the serialized event) through the same `SaveChangesAsync()` call, so they're one atomic transaction — either both exist or neither does. A background poller (`OutboxDispatcherBackgroundService`) reads unprocessed rows, publishes them through the same `IEventPublisher` port the handler used to call directly, and marks them processed. If the process crashes right after the order commits, the outbox row is still there — the poller picks it up on the next run, no event lost. I hand-rolled this rather than reaching for MassTransit's built-in EF outbox specifically so I could point to the mechanics directly in an interview instead of "I enabled a NuGet feature."

**Follow-up: "And duplicate events?"** The outbox still only gives *at-least-once* delivery — if the poller publishes successfully but crashes before marking the row processed, it republishes on the next run, as a brand-new message. I didn't leave that as a hand-wave: `OrderCreatedConsumer` (the one place that actually mutates shared state — stock) checks an `IProcessedOrderStore` keyed by `OrderId` before doing any work at all. That guard is a Postgres row (`EfProcessedOrderStore`, Inventory's own database), not in-process memory, so a restart doesn't forget what's already been decided either. `StockReservedConsumer`/`StockRejectedConsumer` don't need the same table — `Order.MarkConfirmed`/`MarkRejected` are already no-ops once the order has left `Pending`, so idempotency there falls straight out of the domain model. See Q9a and Q8a for where the marker actually gets written and why that turned out to be trickier than it sounds.

**Follow-up: "Why poll instead of publishing immediately after commit?"** I could do both — publish immediately as a latency optimization, with the outbox as the safety net for whatever that immediate attempt misses (a crash before it runs, a broker blip). I kept it poll-only for now: it's simpler to reason about and test, and a couple of seconds of extra latency before Inventory sees an order is a trade-off I'm happy to name in a demo.

### 8a. Inventory's idempotency marker and its stock reservation both write to Postgres — why not just put them in the same transaction and call it done?

**Answer:** That was my first instinct too, and it's an instructive near-miss. The original design had `OrderCreatedConsumer` do three separate steps: reserve stock (its own commit), publish the outcome directly through MassTransit, then mark the order processed (a second, separate commit). A crash between step 1 and step 3 left stock reserved but the order NOT marked processed — a redelivery would see "not yet processed" and reserve the SAME stock again. Double-decrement bug.

The tempting one-line fix — merge "mark processed" into the SAME transaction as the stock UPDATE — only *moves* the gap, it doesn't close it. Now crash between that commit and the (still separate) publish call: the order IS marked processed, but the outcome was never published. A redelivery sees "already processed" and skips straight past ever publishing anything. The order is now stuck `Pending` **forever**, with stock already gone — objectively worse than the bug I was fixing, since the original one at least eventually reached a final order status.

The actual fix is the same idea as the outbox itself: don't publish directly, ever. `StockReservationCoordinator` opens one transaction, reserves stock through `EfStockStore` (which detects the ambient transaction and participates in it instead of opening its own — see Q19a), and — only on success — writes the `ProcessedOrder` marker AND enqueues the outcome event as an outbox row, all before that one transaction commits. A background dispatcher (Inventory's own outbox, mirroring Order Service's) publishes it afterwards. Now there's no pair of "these two things must both happen" writes that can ever disagree — there's exactly one atomic unit, and a separate, already-solved relay problem after it.

**Follow-up: "What about a rejected reservation — no stock changed, does it need the same treatment?"** Yes, but it's a genuinely different case: a rejection means some earlier lines in that attempt may have already had their conditional UPDATE applied (uncommitted) before the failing line. Those must NOT survive, so the coordinator rolls back that transaction entirely on rejection, then records the rejection itself — the marker plus a `StockRejected` outbox row — as a fresh, separate write with zero stock changes involved. Two different code paths for success vs. rejection, but each is internally atomic.

### 8b. What happens if you scale either service to multiple replicas — do the outbox pollers step on each other?

**Answer:** They would have, before I addressed it directly. Every replica runs its own copy of the outbox dispatcher on the same timer; without coordination, two replicas' polls read the SAME unprocessed rows and both publish them. Harmless for correctness (consumers are idempotent — see Q9a), but wasteful, and not something I wanted to just wave away. Against real Postgres, the dispatcher claims its batch with `SELECT ... FOR UPDATE SKIP LOCKED` inside an explicit transaction: each replica's query locks (claims) a disjoint set of rows, silently skipping anything another replica already has locked instead of blocking on it. Standard "Postgres as a job queue" pattern.

**Follow-up: "Why doesn't your test suite exercise that directly?"** Because it genuinely can't without a real Postgres instance — EF's InMemory provider doesn't generate SQL at all, and SQLite has no row-locking syntax to speak of. I made the query itself conditional: `FOR UPDATE SKIP LOCKED` only runs when `_db.Database.IsNpgsql()`; test doubles fall back to a plain LINQ query. That's honest about the boundary — tests prove the dispatch/retry/backoff *logic*, not cross-replica claiming, which would need Testcontainers with real Postgres (same scope cut as everywhere else in this project).

### 8c. What happens if the broker is down for an extended stretch — does the outbox hammer it?

**Answer:** No — a failed publish now gets an exponential backoff (`NextAttemptUtc`, capped at 60 seconds), the same increasing-gaps idea as the RabbitMQ consumer's 1s/5s/15s retry ladder. Before this, a failed row just sat "pending" and got retried on the very next poll tick regardless of how many times it had already failed — fine for a one-off blip, wasteful for a genuine outage.

### 8d. If an outbox message can never be delivered, how would you actually find out?

**Answer:** `GET /diagnostics/outbox/dead-letters` on both services — lists every row whose `RetryCount` has reached `MaxRetries`, along with its last error. Before I added this, a row hitting max retries just became invisible: the dispatcher's query stops selecting it (`RetryCount < MaxRetries`), and there was nothing pointing a human at it. That's inconsistent with a principle I'd already stated elsewhere in this project — "never silently drop a failed message" — which RabbitMQ's `_error` queue already satisfies on the consumer side. The outbox's abandoned rows needed the same visibility, so now they get an `Error`-level log line and a queryable endpoint instead of just sitting quietly in a table.

### 9. How does your retry / error-queue setup work?

**Answer:** The Inventory endpoint has `UseMessageRetry` with intervals 1s / 5s / 15s — increasing gaps to let transient faults heal. If the final attempt still throws, MassTransit moves the message to `inventory-order-created_error` with the exception in its headers, where a human can inspect and replay it. Crucially, out-of-stock is *not* an exception — it's a valid business outcome, so I publish `StockRejected` and complete the message. Retry transient faults, never business outcomes.

**Follow-up: "How would you replay from the error queue?"** RabbitMQ management UI shovel/move back to the original queue, or programmatically. In ASB terms: receive from the DLQ, fix, resubmit.

### 9a. Why key idempotency off `OrderId` instead of MassTransit's `MessageId`?

**Answer:** Transport-level dedupe (MessageId) only protects against the *transport* redelivering the same envelope — a broker requeue after an unacked message, for instance. It does nothing against my own outbox relay legitimately publishing a *new* message that represents the *same* business event: if the relay crashes after `IPublishEndpoint.Publish` succeeds but before it marks its row processed, the next poll republishes that row as a fresh message with a fresh MessageId. `OrderId` is the actual identity of "the thing that must only produce one outcome," so that's what `IProcessedOrderStore` keys on.

**Follow-up: "Why mark it processed only at the end?"** If I marked it at the start, a genuinely failed attempt (say, the DURIAN-1 demo, which throws) would be flagged done even though nothing succeeded — MassTransit's retry ladder would fire again, hit my "already processed" check, and silently skip real work instead of actually retrying. Concretely: the DURIAN-1 throw happens in the consumer *before* it ever calls `StockReservationCoordinator`, so nothing gets marked on that path at all. Check-at-start, mark-only-after-the-coordinator-commits is what keeps retries and duplicate-suppression from fighting each other. See Q8a for why "mark" had to move out of the consumer entirely.

### 10. Explain your JWT flow end to end.

**Answer:** Register/login handlers verify credentials and call `IJwtTokenGenerator`, which signs a token (HS256, symmetric key from config) containing `sub` = user id and an expiry. The client sends it as a Bearer header. The JWT middleware validates signature, issuer, audience, and lifetime *before* any controller runs; `[Authorize]` on `OrdersController` rejects anything invalid with 401. Controllers then read the user id from claims — clients never send a userId in the body, the token *is* the identity.

**Follow-up: "The payload is readable by anyone — is that a problem?"** No, by design: JWTs are signed, not encrypted. Integrity, not confidentiality — so never put secrets in claims, and always use HTTPS in transit.

### 11. How are passwords stored?

**Answer:** Never the password — a PBKDF2 hash: random 16-byte salt per user, 100,000 iterations, SHA-256, stored as `iterations.salt.hash`. Salt kills rainbow tables, iterations make brute force expensive, and storing the iteration count lets me raise it later without invalidating existing users. Verification uses `FixedTimeEquals` to avoid timing side-channels.

**Follow-up: "Why not bcrypt/Argon2?"** They're arguably stronger (memory-hard). PBKDF2 is what ships in the BCL — zero dependencies — and is OWASP-acceptable at this iteration count. I'd happily switch given the package budget.

### 12. Why do failed logins and unknown emails throw the same exception?

**Answer:** Both map to a 401 with the same generic message, so the API can't be used as an email-enumeration oracle — an attacker can't distinguish "wrong password" from "no such account". Same reasoning why `GET /orders/{id}` returns 404 (not 403) for another user's order: a 403 would confirm the id exists.

### 13. What is your global error middleware doing?

**Answer:** It's first in the pipeline, so its try/catch wraps everything downstream. It maps Application exceptions to RFC 7807 Problem Details: `ValidationException`→400 with per-field errors, `NotFoundException`→404, `ConflictException`→409, `InvalidCredentialsException`→401, and anything else→500 with a *generic* body — full details go to the log, never to the client, because stack traces leak internals. One error contract for every endpoint means clients write one error parser.

**Follow-up: "Why middleware over exception filters?"** Middleware catches exceptions from the *whole* pipeline, including non-MVC pieces; filters only cover the MVC slice.

### 14. Why does middleware order matter in your Program.cs?

**Answer:** The pipeline is a chain of nested wrappers — order is behavior. Error handling is registered first so it wraps everything. `UseAuthentication` must precede `UseAuthorization`: you have to establish *who* the caller is before rules about *what* they may do can run. Swap them and every `[Authorize]` endpoint returns 401 even with a valid token.

### 15. Explain EF Core migrations in this project.

**Answer:** The schema lives in versioned C# migration files — `Up()` applies, `Down()` reverts, and the `__EFMigrationsHistory` table records what's applied so `Migrate()` only runs the delta. I call `Migrate()` on startup, which is the right pragmatism for a single-instance local demo: clone, compose up, schema exists. Config details worth knowing: `Total` is a computed property that's deliberately *not* mapped (always derived from lines), status is stored as a string for readable rows, and `Users.Email` has a unique index as the real duplicate-registration guard.

**Follow-up: "Why is startup migration wrong in production?"** Multiple replicas booting simultaneously race to migrate, and app identity shouldn't hold DDL rights. Prod runs migrations as an explicit deploy step (`dotnet ef database update`, a migration bundle, or idempotent SQL scripts reviewed by a DBA).

### 16. Difference between your unit and integration tests?

**Answer:** Unit tests take one handler, real logic, fake everything around it — EF InMemory for the DB, NSubstitute for the publisher — and answer "is the logic right?" in milliseconds. Integration tests boot the *entire real app* with `WebApplicationFactory` and fire actual HTTP: they catch what unit tests structurally can't — a missing `[Authorize]`, middleware in the wrong order, a DI registration typo. My 401-without-token test exists precisely because no unit test can detect a forgotten attribute. Shape: many fast unit tests, a thin layer of integration tests over critical flows.

**Follow-up: "Why SQLite in integration tests instead of the InMemory provider?"** SQLite is a real relational engine — it enforces relational behavior the InMemory provider ignores. Truthfully, closest fidelity is Testcontainers with real Postgres; SQLite is my no-Docker compromise, and I can name it as such.

### 17. How do you test that an event was actually published?

**Answer:** Two levels. Unit: the handler depends on my own `IEventPublisher` interface, so NSubstitute verifies `PublishAsync` was called with the right `OrderCreated` payload. Integration: `AddMassTransitTestHarness()` swaps the transport for an in-memory recorder, and I assert `harness.Published.Any<OrderCreated>()` after a real HTTP POST — proving the *wired* path publishes, not just the class in isolation.

### 18. Why do the two services share no database?

**Answer:** Database-per-service is the microservices rule I'm practicing: shared tables become a hidden synchronous coupling — one team's schema change breaks another team's service. Services integrate only through published contracts (my `OrderFlow.Contracts` events). This isn't just a diagram convention either — Inventory has its own Postgres container (`inventory-db`) with its own migrations, physically separate from Order Service's database, so there's no table either service *could* reach into even by accident.

**Follow-up: "How do you query across services then?"** You don't join — you compose via APIs, or maintain a local read model fed by events (which is where CQRS and messaging meet).

### 19. What's in your docker-compose and why the health-check conditions?

**Answer:** Seven containers: two Postgres instances (one per service, per Q18), RabbitMQ with its management UI, Jaeger for traces, the two .NET services, and the browser-based web UI. The .NET services use `depends_on: condition: service_healthy` for their databases and RabbitMQ, so they start only after `pg_isready` and `rabbitmq-diagnostics ping` succeed — otherwise a service boots first, tries to migrate against a database that isn't accepting connections yet, and crash-loops. Jaeger only gets `condition: service_started`, not a health gate — the OTLP exporter is fire-and-forget, so a service starting before Jaeger is ready just means early spans don't land anywhere, never a crash. Config overrides happen via environment variables (`ConnectionStrings__OrderDb`, `ConnectionStrings__InventoryDb`, `Otlp__Endpoint`), because inside the compose network hosts are service names, not localhost.

**Follow-up: "Why two separate Postgres containers instead of one server with two databases?"** Cost was the same either way locally (both are just `postgres:16-alpine`), so I picked the option that matches production more closely: separate instances mean separate connection pools, separate resource limits, and no shared-server blast radius if one service's queries misbehave. A single shared server with two logical databases would still *look* like database-per-service in the model, but the isolation would be an illusion.

**Follow-up: "Why no healthchecks on your own containers?"** The aspnet base image ships no curl/wget. Both services still expose `/health` (the API's includes a DB check) for anything that can probe them. Documented trade-off.

### 19a. How does EfStockStore avoid overselling stock without an app-level lock?

**Answer:** The old in-memory version used a `lock` around "check every line, then decrement every line" — correct, but only within one process; the moment you run two replicas, that lock stops meaning anything. `EfStockStore` instead reserves each line with a single conditional UPDATE: `SET "AvailableQuantity" = "AvailableQuantity" - @qty WHERE "ProductId" = @id AND "AvailableQuantity" >= @qty` (via EF Core's `ExecuteUpdateAsync`). The check and the write are the *same* statement, so there's no read-then-decide-then-write window — Postgres's own row-level locking on that UPDATE serializes two concurrent reservations for the same product: whichever commits first wins, and the second one's WHERE clause re-evaluates against the now-lower quantity.

**Follow-up: "What makes the whole order ALL-OR-NOTHING then?"** All of an order's lines run inside one explicit transaction. The instant any line's UPDATE affects zero rows, I roll back — which undoes every line this same attempt already decremented, not just the failing one. No half-reserved orders.

**Follow-up: "How did you test that without Testcontainers/real Postgres?"** SQLite for the deterministic logic (successful reserve, unknown product, and — the important one — a multi-line reservation where an earlier line succeeds but a later one fails, asserting the earlier line's stock came back exactly as it was). I was explicit in the test file's comments that true concurrent-access safety is a property of Postgres's row locking specifically, which a single SQLite connection can't meaningfully stress-test — that'd need Testcontainers with real Postgres, which I scoped out for this project the same way I did for the integration tests.

**Follow-up: "Any other concurrency issue in there?"** Yes — a deadlock, not a lost update. If Order A reserves [APPLE-1, then MANGO-1] and Order B concurrently reserves [MANGO-1, then APPLE-1], each can end up holding the row the other is waiting for — classic circular wait, and Postgres's deadlock detector aborts one of them. The fix is standard: sort every reservation's lines into the same canonical order (`OrderBy(l => l.ProductId)`) before acquiring any locks. Every caller then asks for locks in the same order, so the cycle can't form. Cheap, no downside, and the kind of fix you only think of if you've been burned by it before or know to look for it.

**Follow-up: "Does EfStockStore always own its own transaction?"** Not anymore — it detects whether one is already open (`_db.Database.CurrentTransaction`) and participates in it if so, instead of always calling `BeginTransactionAsync` itself. That's what lets `StockReservationCoordinator` (Q8a) wrap the stock update, the idempotency marker, and the outbox row in one transaction it owns, while `EfStockStore` still works standalone for its own tests exactly as before.

### 19b. How would you debug a single order that went wrong, given it touches two services?

**Answer:** This is exactly what I added distributed tracing for. Both services export traces via OpenTelemetry over OTLP to Jaeger (`docker-compose` service `jaeger`, UI on :16686). One order shows up as ONE trace, not two services' worth of logs I have to correlate by timestamp: the REST call, the Postgres write (order + outbox row), the RabbitMQ publish, Inventory's consumer, its Postgres write, its publish back, and the final status update — one waterfall. I get most of this almost for free: ASP.NET Core instrumentation traces the inbound HTTP request, `Npgsql.OpenTelemetry` traces every SQL command against either database with no EF-specific package needed, and MassTransit already publishes spans on its own `"MassTransit"` ActivitySource and propagates trace context through message headers — I just had to add `AddSource("MassTransit")` for the publish-here/consume-there spans to land in the same trace.

**Follow-up: "What's the one non-obvious thing you hit building this?"** `tracing.AddNpgsql()` was ambiguous — OpenTelemetry's hosting builder implements *both* `TracerProviderBuilder` and `IServiceCollection`, and EF Core ships its own unrelated `AddNpgsql<TContext>(IServiceCollection)` extension. Both were in scope, and the compiler picked the wrong one. Fixed by calling `Npgsql.TracerProviderBuilderExtensions.AddNpgsql(tracing)` explicitly instead of relying on extension-method syntax. Small thing, but the kind of "two packages you'd never expect to collide, collide" bug that's genuinely faster to have hit once than to reason about from first principles.

**Follow-up: "What if Jaeger isn't running?"** Non-fatal by design — the OTLP exporter just fails to connect in the background and the app runs normally. I rely on that: `dotnet run` locally without `docker compose up` shouldn't break because a trace collector isn't there.

### 20. What would you do differently for production?

**Answer:** In priority order: secrets out of appsettings into a vault/env injection, asymmetric JWT keys or delegating auth to an identity provider, migrations as a deploy step, and a distributed rate limiter (Redis-backed) instead of one that only knows about its own instance. (Transactional outbox — with multi-replica-safe claiming and backoff — idempotent consumers, persistent inventory, a CI pipeline, distributed tracing, RabbitMQ health checks, and rate limiting used to top this list; all seven are implemented now — see Q8/Q8a–d/Q9a, Q18/Q19a, Q19b, Q21, and the GitHub Actions workflow.) I kept the rest out deliberately — the README says so — because a junior project that *knows* its cuts beats one that pretends to be Netflix.

**Follow-up: "Walk me through your CI pipeline."** Two jobs on every push/PR to master. `test` restores, builds, and runs the full solution's tests in Release — no Docker needed, because the integration tests already swap Postgres/RabbitMQ for SQLite and MassTransit's in-memory test harness for exactly this reason (fast, hermetic CI). `docker` then runs `docker compose build` so a broken Dockerfile fails the pipeline even if the plain .NET build was fine — that's a real gap otherwise, since `dotnet build` and `docker build` can diverge (missing COPY paths, wrong working directory, etc.).

### 21. How do you protect these endpoints from abuse, and how do you know if the broker goes down?

**Answer:** Two separate things. First, rate limiting: ASP.NET Core's built-in `Microsoft.AspNetCore.RateLimiting` middleware, partitioned per caller rather than one shared counter — `/api/v1/auth/*` allows 5 requests/minute per IP (the only identity available before authentication; brute-force protection on login, abuse protection on mass registration), and `POST /api/v1/orders` allows 20/10s per authenticated user, keyed off the JWT's `sub` claim — same "the token is the identity" principle the rest of the app already follows. Both return `429`. Second, health checks: `/health` used to only check the database; I added a RabbitMQ check (`AddRabbitMQ(...)`) so a broker outage actually flips the service to `Unhealthy` instead of it silently failing every publish while still reporting fine.

**Follow-up: "Why partition by caller instead of one global limit?"** A single global counter means one abusive client exhausts the budget for every legitimate user hitting the same endpoint — the fix would become the outage. Partitioning means an attacker only ever throttles themselves.

**Follow-up: "Does this survive multiple replicas?"** No, and I say so directly — each replica's rate limiter keeps its own in-memory counters, so a real multi-replica deployment would let through roughly `N × PermitLimit` requests across `N` replicas instead of one shared limit. The production fix is a distributed limiter backed by something like Redis. Listed honestly in the README's "what's next", not hidden.

---

**Meta-tip:** the strongest sentence in any of these answers is the trade-off sentence. Interviewers probe for whether you understand *costs*, not just patterns. Every "why X?" answer above ends with what X costs — keep that habit.
