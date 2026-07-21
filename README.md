# OrderFlow 🍎

[![CI](https://github.com/Walidso/OrderFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/Walidso/OrderFlow/actions/workflows/ci.yml)

A small but complete **event-driven microservices system** built with .NET 8 — created as a hands-on portfolio project to demonstrate Clean Architecture, CQRS, async messaging, testing, and containerization.

**The flow in one sentence:** a user registers, logs in, and places an order via a REST API; the order is saved as `Pending` and an event is published; a separate Inventory service consumes it, reserves stock, and answers with an event that flips the order to `Confirmed` or `Rejected`.

## Architecture

```mermaid
flowchart LR
    Client([Client / Swagger]) -->|"JWT + REST"| API

    subgraph OrderService["Order Service (Clean Architecture)"]
        API[Api layer] --> APP[Application layer<br/>CQRS + MediatR + FluentValidation]
        APP --> DOM[Domain layer<br/>Order, User entities]
        APP --> INF[Infrastructure layer<br/>EF Core, JWT, MassTransit]
    end

    INF -->|SQL| PG[(PostgreSQL<br/>Order Service)]
    INF -->|"publish OrderCreated"| MQ[[RabbitMQ]]
    MQ -->|consume| INV[Inventory Service<br/>MassTransit worker]
    INV -->|SQL| PG2[(PostgreSQL<br/>Inventory Service)]
    INV -->|"StockReserved / StockRejected"| MQ
    MQ -->|consume| INF
```

## Run it (3 commands)

```bash
git clone <this-repo> && cd OrderFlow
docker compose up --build
# then open:
open http://localhost:5001/swagger
```

That's it — migrations apply automatically on startup.

**Web UI:** http://localhost:5003 — register/login, place orders with one-click presets (happy path, insufficient stock, poison message, mangled token), watch order status and live stock update in the browser. No curl or Swagger needed.

**Or try the full flow in Swagger:**
1. `POST /api/v1/auth/register` → copy the `token`
2. Click **Authorize**, paste the token
3. `POST /api/v1/orders` with `productId: "APPLE-1"`, quantity 3
4. `GET /api/v1/orders/{id}` → watch status go `Pending` → `Confirmed`
5. Peek at stock dropping: http://localhost:5002/stock
6. RabbitMQ UI: http://localhost:15672 (guest / guest)
7. Jaeger UI: http://localhost:16686 → pick `order-service` → find the trace → watch it span the REST call, the Postgres write, the RabbitMQ publish, and Inventory's consumer as one waterfall

**Break it on purpose (the fun part) — via the web UI presets or manually:**
- Order `MANGO-1` with quantity 5 → order becomes `Rejected` (only 3 in stock)
- Order `DURIAN-1` → the Inventory consumer throws, retries 3× (watch its logs), then the message lands in the `inventory-order-created_error` queue — RabbitMQ's equivalent of a dead-letter queue
- Send a request with a mangled token → `401` before any handler runs

## Run the tests

```bash
dotnet test
```

- **Order Service unit tests** (`tests/OrderService.UnitTests`) — handlers in isolation, EF InMemory + NSubstitute mocks, milliseconds each
- **Order Service integration tests** (`tests/OrderService.IntegrationTests`) — the real app booted in memory via `WebApplicationFactory`, real HTTP, real JWT validation, SQLite instead of Postgres, MassTransit test harness instead of RabbitMQ. No Docker needed.
- **Inventory Service unit tests** (`tests/InventoryService.UnitTests`) — `OrderCreatedConsumer` tested by mocking `ConsumeContext<T>` and `IStockReservationCoordinator` directly (no bus needed); `EfStockStore`, `StockReservationCoordinator`, and Inventory's own `OutboxDispatcher` tested against SQLite (a real relational engine, no Docker) since their atomic-update logic doesn't translate against EF's InMemory provider.

## Tech decisions

| Decision | Why | Trade-off accepted |
|---|---|---|
| Clean Architecture (4 layers) | Business logic testable without HTTP/DB; dependencies point inward only | More projects/ceremony than a small CRUD app strictly needs |
| CQRS with MediatR | One handler = one use case = one focused unit test; validation as a pipeline behavior applies to every command automatically | Indirection: request flow is less obvious than a direct service call |
| RabbitMQ + MassTransit | Free, runs locally in Docker; MassTransit's abstractions (retry, error queues) map 1:1 to Azure Service Bus concepts | No broker-native dead-lettering — MassTransit emulates it with `_error` queues |
| PostgreSQL + EF Core migrations | Real relational DB, schema versioned in code, `Migrate()` on startup for zero-friction demo | Startup migration is wrong for multi-replica prod (race conditions) |
| JWT (symmetric HS256) | Stateless auth, easy to demo, standard claims flow | Single shared key; prod at scale would use asymmetric keys / an identity provider |
| PBKDF2 password hashing | Built into .NET, no dependency, salted + 100k iterations | Argon2/bcrypt are stronger choices if adding a package is acceptable |
| Global exception middleware → RFC 7807 | One error contract for all endpoints; no leaked stack traces | — |
| Inventory's own PostgreSQL | Database-per-service kept honest instead of an in-memory dictionary; stock and the idempotency guard both survive a restart; a conditional `UPDATE ... WHERE AvailableQuantity >= @qty` inside a transaction replaces an in-process lock, so reservation stays correct even with multiple replicas | A second Postgres container (`inventory-db`) — cheap locally, but two databases to operate instead of one |
| Hand-rolled Transactional Outbox (both services) | Save + publish become one atomic `SaveChangesAsync()`; a background poller relays events, closing the "order saved but event lost" gap | MassTransit ships an EF outbox that does this with less code — hand-rolled so the mechanics are visible and explainable, not hidden behind a NuGet feature flag |
| `FOR UPDATE SKIP LOCKED` for outbox claiming | Lets multiple replicas of either service poll the outbox concurrently without duplicate work — each replica's transaction locks (claims) a disjoint batch, skipping rows another replica already has locked | Postgres-only SQL (`FromSqlInterpolated`); test doubles (SQLite/InMemory) fall back to a plain query, since they only need to prove dispatch/retry logic, not cross-replica claiming |
| OpenTelemetry + Jaeger (OTLP) | Free, single container, standard vendor-neutral SDK; MassTransit and Npgsql both ship their own instrumentation, so most of the wiring is a few `TracerProviderBuilder` calls, not custom code | One more container to run locally; a managed backend (Honeycomb, Azure Monitor, Datadog) would just be a different OTLP endpoint, not a rewrite |
| ASP.NET Core rate limiting (built-in middleware) | Brute-force/DoS protection on `/auth/*` and `POST /orders`, partitioned per caller so one abusive client can't lock out everyone; zero extra packages | A single-instance in-memory limiter — multiple replicas would each enforce their own window instead of sharing one counter (a distributed limiter, e.g. backed by Redis, would be the production fix) |

## Project layout

```
src/
  BuildingBlocks/OrderFlow.Contracts/   # shared event records (the ONLY shared code)
  OrderService/
    OrderService.Domain/          # entities, zero dependencies
    OrderService.Application/     # CQRS handlers, validators, interfaces
    OrderService.Infrastructure/  # EF Core, migrations, JWT, MassTransit
    OrderService.Api/             # controllers, middleware, Program.cs
  InventoryService/
    InventoryService.Worker/      # MassTransit consumer, EF Core stock + idempotency + outbox, own Postgres
web/                              # static HTML/CSS/JS UI, served by nginx
tests/
  OrderService.UnitTests/
  OrderService.IntegrationTests/
  InventoryService.UnitTests/
```

## Transactional Outbox

`CreateOrderCommandHandler` used to save the order, then publish `OrderCreated` in a second, separate step — a crash between the two left a `Pending` order no one would ever process. That gap is closed, and both Order Service and Inventory Service now have their own outbox (see "Persistent inventory" below for why Inventory needed one too):

- The handler writes the order **and** an `OutboxMessage` row (the serialized event) through the **same** `SaveChangesAsync()` call — one transaction, so either both commit or neither does.
- A background poller (`OutboxDispatcherBackgroundService`, `Infrastructure/Outbox/`) claims a batch of unprocessed rows every couple of seconds and publishes them. Against real Postgres it claims rows with `SELECT ... FOR UPDATE SKIP LOCKED` inside a transaction — so if this service ever runs as multiple replicas, each poller locks a disjoint batch instead of two replicas publishing the same row. Test doubles (SQLite/EF InMemory) fall back to a plain query, since they only need to prove dispatch/retry logic, not cross-replica claiming.
- A failed publish gets an **exponential backoff** (`NextAttemptUtc`, capped at 60s) instead of being retried on the very next tick — same increasing-gaps idea as the RabbitMQ consumer's 1s/5s/15s ladder. After a configurable `MaxRetries`, a row is left in place with its last error — the outbox's version of a dead-letter queue — and logged at `Error` level. `GET /diagnostics/outbox/dead-letters` on both services lists anything abandoned like this, so "never silently drop a failed message" (already the rule for RabbitMQ's `_error` queue) applies here too.

See `src/OrderService/OrderService.Application/Orders/Commands/CreateOrder/CreateOrderCommandHandler.cs` and `src/OrderService/OrderService.Infrastructure/Outbox/` for the implementation, and `tests/OrderService.UnitTests/OutboxDispatcherTests.cs` for the retry/backoff/poison-message behavior under test.

## Idempotent consumers

The outbox above guarantees *at-least-once* delivery, not exactly-once — a redelivered `OrderCreated` is not a rare edge case, it's an expected consequence of the pattern. Without a guard, `OrderCreatedConsumer` would call `TryReserveAsync()` twice for one order and double-decrement stock.

- **Inventory side (the real risk):** `OrderCreatedConsumer` checks an `IProcessedOrderStore` keyed by `OrderId` — not MassTransit's transport `MessageId`, since a re-published message gets a fresh one — before doing any work at all. `EfProcessedOrderStore` persists this to Inventory's own Postgres, so the guard survives a restart too. A thrown exception (the `DURIAN-1` demo) happens *before* any of this, so MassTransit's own retry ladder still retries for real.
- **Where the marker actually gets written matters just as much as checking it** — see "Persistent inventory" below for why the write moved out of the consumer and into `StockReservationCoordinator`, atomically with the stock change and the outbox row.
- **Order Service side (already safe):** `StockReservedConsumer`/`StockRejectedConsumer` don't need a separate tracking table — `Order.MarkConfirmed()`/`MarkRejected()` are no-ops once the order has left `Pending`, so a duplicate delivery just re-applies a state transition that's already happened. Idempotency here falls out of the domain model itself.

See `src/InventoryService/InventoryService.Worker/Idempotency/` and `tests/InventoryService.UnitTests/OrderCreatedConsumerTests.cs`.

## Persistent inventory

Inventory used to keep stock in an in-memory `Dictionary` — the whole "database" was a field in a singleton, gone on every restart, and only ever correct for a single instance of the service. It now has its own Postgres (`inventory-db` in docker-compose, migrated at startup exactly like Order Service's), separate from Order Service's — database-per-service stays a real boundary, not just a talking point.

**How `EfStockStore` keeps ALL-OR-NOTHING reservation safe without an in-process lock:**

- Each line is reserved with a single conditional `UPDATE`: `SET "AvailableQuantity" = "AvailableQuantity" - @qty WHERE "ProductId" = @id AND "AvailableQuantity" >= @qty`. The check and the write are the *same* statement — there's no read-then-decide-then-write window for another transaction to land in. Postgres's own row-level locking on that `UPDATE` is what makes two concurrent reservations for the same scarce product resolve correctly, without any app-level `lock`.
- All of an order's lines run inside one transaction, so the moment any line's `UPDATE` affects zero rows, the whole reservation rolls back — including lines that already succeeded earlier in the same attempt. No half-reserved orders.
- **Lines are sorted by `ProductId` before acquiring any locks** — deadlock prevention. Two orders reserving the same products in different sequences (Order A: APPLE-1 then MANGO-1; Order B: MANGO-1 then APPLE-1) is the textbook circular-wait deadlock if they run concurrently. Sorting guarantees every caller acquires row locks in the same canonical order, so that cycle can't form.

**Why stock, idempotency, and the outbox write all had to become ONE transaction:** the original design had the consumer do three separate steps — reserve stock (its own commit), publish the outcome directly, then mark the order processed (a second commit). A crash between step 1 and step 3 left stock reserved but the order not-yet-marked, so a redelivery would reserve the SAME stock again. The tempting quick fix — just merge "mark processed" into the stock transaction — only *moves* the gap: crash between that commit and the (still separate) publish call, and the order is now stuck `Pending` forever, since a redelivery sees "already processed" and skips straight past ever publishing the outcome. Worse than the original bug.

`StockReservationCoordinator` (`Reservations/`) is the actual fix, and it's the same idea as the outbox above: don't publish directly at all. It opens one transaction, calls `EfStockStore` (which detects the ambient transaction and participates in it instead of committing its own), and — only if the reservation succeeded — writes the `ProcessedOrder` marker and enqueues the outcome event in that SAME transaction before committing. A rejection is a separate case: any partial stock changes from that attempt are rolled back first (they must not survive), and the rejection itself is recorded as a fresh, independent write with no stock involved.

See `src/InventoryService/InventoryService.Worker/Stock/EfStockStore.cs`, `src/InventoryService/InventoryService.Worker/Reservations/StockReservationCoordinator.cs`, and `tests/InventoryService.UnitTests/StockReservationCoordinatorTests.cs` (the rollback test proves an already-applied line gets undone when a later line fails, and that both success and rejection commit the marker + outbox row atomically with whatever happened to stock).

## Distributed tracing

Before this, understanding what happened to one order meant reading two services' logs side by side and matching timestamps by eye. Both services now export traces (OpenTelemetry → OTLP → Jaeger, `http://localhost:16686`), so a single order shows up as **one trace** spanning:

`POST /api/v1/orders` → the Postgres `INSERT` (order + outbox row, same span-parent since it's the same `SaveChangesAsync()`) → the outbox relay's RabbitMQ publish → Inventory's `OrderCreatedConsumer` → its Postgres `UPDATE` → its RabbitMQ publish back → `StockReservedConsumer`/`StockRejectedConsumer` → the final Postgres `UPDATE` that flips the order's status.

- **ASP.NET Core instrumentation** traces the inbound HTTP request (with `/health` filtered out — it's polled constantly and adds nothing but noise).
- **Npgsql's own instrumentation** (`Npgsql.OpenTelemetry`) traces every SQL command against either database — no separate EF Core-specific package needed.
- **MassTransit publishes its own spans** on an ActivitySource literally named `"MassTransit"`. Listening for that source is the one line (`AddSource("MassTransit")`) that makes the publish in one service and the consume in the other join the *same* trace instead of being two unrelated ones — MassTransit propagates the trace context through the message headers for you.

The one non-obvious line in both services' setup: OpenTelemetry's hosting builder (`WithTracing(...)`) implements *both* `TracerProviderBuilder` and `IServiceCollection`, which collides with EF Core's own unrelated `AddNpgsql<TContext>(IServiceCollection)` extension under normal `tracing.AddNpgsql()` syntax — worth calling out if it comes up, see the comment in `OrderService.Infrastructure/DependencyInjection.cs`.

If Jaeger isn't running (e.g. `dotnet run` without `docker compose`), the OTLP exporter just fails to connect in the background — non-fatal, the app runs normally, you simply don't get traces.

## Rate limiting & health checks

- **Rate limiting** — ASP.NET Core's built-in `Microsoft.AspNetCore.RateLimiting` middleware, partitioned per caller rather than one global counter (a single abusive client shouldn't lock out everyone else): `/api/v1/auth/*` (register + login) allows 5 requests/minute per IP — the only identity available pre-authentication; `POST /api/v1/orders` allows 20/10s per authenticated user (the JWT's `sub` claim), matching how the rest of the app already treats the token as the caller's real identity. Both return `429 Too Many Requests`. See `tests/OrderService.IntegrationTests/RateLimitingTests.cs`.
- **RabbitMQ health checks** — `/health` on both services used to only check the database; a broker outage could leave a service reporting `Healthy` while every publish or consume silently failed. `AddRabbitMQ(...)` (the `AspNetCore.HealthChecks.Rabbitmq` package) closes that gap.

## CI

`.github/workflows/ci.yml` runs on every push/PR to `master`, as two jobs:

- **test** — restore, build, and `dotnet test` the whole solution in Release. No Docker involved: the integration tests already swap Postgres/RabbitMQ for SQLite + MassTransit's in-memory test harness specifically so CI doesn't need them. Trx results for all three test projects are uploaded as a build artifact.
- **docker** — `docker compose build`, so a broken Dockerfile fails CI even when the .NET build itself is fine.

## What I would improve next

Honest scope: this is a learning/portfolio project, so these were deliberately left out —

1. **API gateway** — a single entry point (e.g. YARP) in front of both services. Rate limiting is already in place directly on Order Service (see above); a gateway would centralize it (and routing, and TLS termination) rather than each service handling its own.
2. **Distributed rate limiting** — the current limiter's counters live in each instance's memory; a real multi-replica deployment would need a shared store (e.g. Redis) so all replicas enforce one limit together.

See `INTERVIEW_DEFENSE.md` for how I'd talk about every decision in an interview.
