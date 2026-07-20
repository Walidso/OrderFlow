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

**Follow-up: "And duplicate events?"** The outbox still only gives *at-least-once* delivery — if the poller publishes successfully but crashes before marking the row processed, it republishes on the next run, as a brand-new message. I didn't leave that as a hand-wave: `OrderCreatedConsumer` (the one place that actually mutates shared state — stock) now checks an `IProcessedOrderStore` keyed by `OrderId` before doing any work, and only marks it after successfully publishing the outcome. `StockReservedConsumer`/`StockRejectedConsumer` don't need the same table — `Order.MarkConfirmed`/`MarkRejected` are already no-ops once the order has left `Pending`, so idempotency there falls straight out of the domain model. See Q9a.

**Follow-up: "Why poll instead of publishing immediately after commit?"** I could do both — publish immediately as a latency optimization, with the outbox as the safety net for whatever that immediate attempt misses (a crash before it runs, a broker blip). I kept it poll-only for now: it's simpler to reason about and test, and a couple of seconds of extra latency before Inventory sees an order is a trade-off I'm happy to name in a demo.

### 9. How does your retry / error-queue setup work?

**Answer:** The Inventory endpoint has `UseMessageRetry` with intervals 1s / 5s / 15s — increasing gaps to let transient faults heal. If the final attempt still throws, MassTransit moves the message to `inventory-order-created_error` with the exception in its headers, where a human can inspect and replay it. Crucially, out-of-stock is *not* an exception — it's a valid business outcome, so I publish `StockRejected` and complete the message. Retry transient faults, never business outcomes.

**Follow-up: "How would you replay from the error queue?"** RabbitMQ management UI shovel/move back to the original queue, or programmatically. In ASB terms: receive from the DLQ, fix, resubmit.

### 9a. Why key idempotency off `OrderId` instead of MassTransit's `MessageId`?

**Answer:** Transport-level dedupe (MessageId) only protects against the *transport* redelivering the same envelope — a broker requeue after an unacked message, for instance. It does nothing against my own outbox relay legitimately publishing a *new* message that represents the *same* business event: if the relay crashes after `IPublishEndpoint.Publish` succeeds but before it marks its row processed, the next poll republishes that row as a fresh message with a fresh MessageId. `OrderId` is the actual identity of "the thing that must only produce one outcome," so that's what `IProcessedOrderStore` keys on.

**Follow-up: "Why mark it processed only at the end?"** If I marked it at the start, a genuinely failed attempt (say, the DURIAN-1 demo, which throws) would be flagged done even though nothing succeeded — MassTransit's retry ladder would fire again, hit my "already processed" check, and silently skip real work instead of actually retrying. Check-at-start, mark-only-after-success is what keeps retries and duplicate-suppression from fighting each other.

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

**Answer:** Database-per-service is the microservices rule I'm practicing: shared tables become a hidden synchronous coupling — one team's schema change breaks another team's service. Services integrate only through published contracts (my `OrderFlow.Contracts` events). Inventory being in-memory is the honest demo shortcut; the boundary is what matters.

**Follow-up: "How do you query across services then?"** You don't join — you compose via APIs, or maintain a local read model fed by events (which is where CQRS and messaging meet).

### 19. What's in your docker-compose and why the health-check conditions?

**Answer:** Four containers: Postgres, RabbitMQ with management UI, and the two services. The .NET services use `depends_on: condition: service_healthy`, so they start only after `pg_isready` and `rabbitmq-diagnostics ping` succeed — otherwise the API boots first, tries to migrate against a database that isn't accepting connections, and crash-loops. Config overrides happen via environment variables (`ConnectionStrings__OrderDb`), because inside the compose network hosts are service names, not localhost.

**Follow-up: "Why no healthchecks on your own containers?"** The aspnet base image ships no curl/wget. Both services still expose `/health` (the API's includes a DB check) for anything that can probe them. Documented trade-off.

### 20. What would you do differently for production?

**Answer:** In priority order: secrets out of appsettings into a vault/env injection, asymmetric JWT keys or delegating auth to an identity provider, migrations as a deploy step, persistent inventory DB (which would also make my in-memory idempotency guard survive a restart), and OpenTelemetry tracing across services. (Transactional outbox, idempotent consumers, and a CI pipeline used to top this list; all three are implemented now — see Q8, Q9a, and the GitHub Actions workflow.) I kept the rest out deliberately — the README says so — because a junior project that *knows* its cuts beats one that pretends to be Netflix.

**Follow-up: "Walk me through your CI pipeline."** Two jobs on every push/PR to master. `test` restores, builds, and runs the full solution's tests in Release — no Docker needed, because the integration tests already swap Postgres/RabbitMQ for SQLite and MassTransit's in-memory test harness for exactly this reason (fast, hermetic CI). `docker` then runs `docker compose build` so a broken Dockerfile fails the pipeline even if the plain .NET build was fine — that's a real gap otherwise, since `dotnet build` and `docker build` can diverge (missing COPY paths, wrong working directory, etc.).

---

**Meta-tip:** the strongest sentence in any of these answers is the trade-off sentence. Interviewers probe for whether you understand *costs*, not just patterns. Every "why X?" answer above ends with what X costs — keep that habit.
