# Event Ticketing API

A RESTful API for a simplified event ticketing system: event management, ticket
purchasing, availability, and sales reporting.

.NET 10 · ASP.NET Core (controllers) · EF Core 10 · PostgreSQL 18

Everything in the brief is standard CRUD except one line — **prevent
overselling**. That is a cross-record invariant under concurrency, and it is the
decision every other choice here follows from.

---

## Running it

Prerequisites: .NET SDK 10, and Podman or Docker.

```bash
# 1. Database
podman compose -f LocalInfra/docker-compose.yml up -d

# 2. Schema
dotnet tool restore
dotnet ef database update \
  --project Ripple.Treasury.Assessment.Infrastructure \
  --startup-project Ripple.Treasury.Assessment.Api

# 3. API
dotnet run --project Ripple.Treasury.Assessment.Api
```

| | |
|---|---|
| API | http://localhost:5205 |
| API reference (Scalar) | http://localhost:5205/scalar |
| OpenAPI document | http://localhost:5205/openapi/v1.json |
| Liveness | http://localhost:5205/health/live |
| Readiness | http://localhost:5205/health/ready |

### Connection

Defaults are in `LocalInfra/docker-compose.yml` and
`appsettings.Development.json`, and every value has a `${VAR:-default}` override.

```
Host=localhost;Port=5432;Database=ripple_treasury;Username=ripple;Password=ripple_local_dev
```

---

## Layout

```
Ripple.Treasury.Assessment.Api
    Controllers/          events (incl. purchase + sales report), purchases, health
    Models/Requests/      DataAnnotations and IValidatableObject rules
    Mapping/              request -> input, hand written
    ErrorHandling/        exception -> ProblemDetails

Ripple.Treasury.Assessment.Services
    *Service.cs           business operations, invariants, transaction boundaries.
                          The pure parts - pricing, capacity, fingerprint - are
                          static methods on the service, so they unit test without
                          a database
    Inputs/               what a write operation needs
    Projections/          what a read operation returns
    Exceptions/           domain failures the Api maps to status codes

Ripple.Treasury.Assessment.Infrastructure
    Entities/             POCOs with mapping attributes
    Enums/
    Migrations/
    TicketingDbContext    fluent config for what attributes cannot express

Ripple.Treasury.Assessment.UnitTests          pure logic, no database
Ripple.Treasury.Assessment.IntegrationTests   Testcontainers PostgreSQL

LocalInfra/                                   docker-compose for PostgreSQL
```

Dependency flow is `Api → Services → Infrastructure`, enforced by project
references. The Api never touches `DbContext`; that single rule is what makes the
Services layer worth having rather than decorative.

---

## API

| | | |
|---|---|---|
| `POST` | `/events` | 201, creates the event with its tiers and seeds inventory |
| `GET` | `/events?from=&venue=&page=&pageSize=` | 200, paged list |
| `GET` | `/events/{id}` | 200, with pricing tiers |
| `PUT` | `/events/{id}` | 200, resizes inventory; 409 if a tier would drop below what is sold |
| `DELETE` | `/events/{id}` | 204, hard delete if nothing sold, otherwise `Cancelled` |
| `POST` | `/events/{id}/publish` | 200, `Draft` to `Published` |
| `GET` | `/events/{id}/availability` | 200, per-tier remaining, counted from inventory |
| `POST` | `/events/{id}/purchases` | 201 or 200, requires an `Idempotency-Key` header |
| `GET` | `/events/{id}/sales-report` | 200, totals and per-tier breakdown |
| `GET` | `/purchases/{id}` | 200, items and ticket ids |
| `GET` | `/health/live`, `/health/ready` | 200, or 503 when the database is unreachable |

Routes are not prefixed with `/api` — the host already scopes the service.

### Purchasing is idempotent

`POST /events/{id}/purchases` requires an `Idempotency-Key` header. A client that
times out and retries must not buy twice.

| Case | Response |
|---|---|
| New key | `201 Created` |
| Same key, same request | `200 OK` with `Idempotent-Replay: true` |
| Same key, different request | `422 Unprocessable Entity` |

A SHA-256 fingerprint of the normalised request is used for checking for idempotency.

---

## The data model

Five tables, three grains.

| Table | Purpose |
|---|---|
| `events` | The show: name, venue, when, total capacity, lifecycle status |
| `pricing_tiers` | A named price band within an event, with its price and slice of capacity |
| `tickets` | Materialised inventory. One row per sellable unit — the rows `SKIP LOCKED` sells from |
| `purchases` | Transaction header. One row per purchase, owning the idempotency key and total |
| `purchase_items` | What was bought per tier, at a price frozen as of purchase time |

`purchases` answers *what transaction occurred*, `purchase_items` answers *what
was agreed at what price*, `tickets` answers *which specific units are allocated*.

`purchase_items.quantity` is derivable from `tickets` and is kept anyway. That
redundancy is deliberate: `SUM(purchase_items.quantity)` must equal
`COUNT(tickets WHERE status = 'Sold')` for an event, giving two independently
maintained records of the same fact that can be checked against each other.
`unit_price` is **not** derivable — tier prices change, and historical revenue
must not move when an admin edits pricing. That column is why the table exists.

---

## Design decisions and trade-offs

### One row per seat, not a counter

Creating an event creates a `tickets` row for every seat it sells.

The obvious alternative is a counter — one number per tier, incremented on each
sale. It is cheaper and it works. We keep rows instead for three reasons:

- **`SKIP LOCKED` needs rows.** Two buyers can take different seats at the same
  time because each locks the rows it is taking and skips the ones the other
  holds. With a single counter there is nothing to skip; everyone queues.
- **A counter can go wrong quietly.** If it ever disagrees with what was actually
  sold, there is nothing to compare it against. Rows can be counted and checked
  against `purchase_items`, which is what the oversell test does.
- **Rows can be pointed at.** A seat can be assigned, transferred or refunded on
  its own. A counter only knows how many.

The cost is real: a 50,000-seat event writes 50,000 rows up front, about 11 MB,
in roughly 700 ms. That happens even for an event nobody publishes.

**Also considered:** creating the rows on publish rather than on create. It saves
that write for abandoned drafts, but then a tier is either "has rows" or "does
not", and both update and publish have to handle each case.

### Selling tickets without overselling

```sql
WITH selling AS (
    SELECT id FROM tickets
    WHERE pricing_tier_id = @tier_id AND status = 'Available'
    ORDER BY seat_ordinal LIMIT @quantity
    FOR UPDATE SKIP LOCKED
)
UPDATE tickets t SET status = 'Sold', purchase_id = @purchase_id, sold_at = now()
FROM selling s WHERE t.id = s.id
RETURNING t.id;
```

`SKIP LOCKED` is the whole trick. A buyer takes the first N seats nobody else is
holding, and walks past the ones that are taken instead of waiting for them. Two
people buying at once get different seats and neither is delayed.

Getting back fewer rows than asked for means the tier ran out. There is no count
to check first and no window between checking and taking — the rows come back
already sold.

**Also considered:**

- **Lock the tier row.** Correct, but then every buyer for that tier waits in
  line. How fast you can sell becomes how fast one purchase finishes.
- **Optimistic retry.** Read, write, retry on conflict. Correct, but the busier
  the event the more everyone retries — worst behaviour exactly when it matters.
- **A counter.** Covered above.

`ix_tickets_available` indexes only the unsold rows, in seat order, so the query
reads them straight off the index without sorting. At 50,000 seats with 45,000
sold: **3 page reads, 0.041 ms.**

### Resizing a tier: one at a time per event

Resizing a tier touches many rows at once — counting what is sold, adding seats,
removing unsold ones — so `EventService` locks the event row first and holds it
until the change is done. One resize at a time per event.

Without it, two people growing the same tier both read the same highest seat
number and both try to add seats from there. There is a test for this: remove the
lock and it fails with `23505 duplicate key value violates unique constraint
"uq_tickets_tier_ordinal"`.

### Three kinds of lock, three different problems

| What is being protected | How |
|---|---|
| Selling tickets | `FOR UPDATE SKIP LOCKED` on the ticket rows |
| Resizing a tier | `FOR UPDATE` on the event row |
| Reusing an idempotency key | `pg_advisory_xact_lock` on the key, plus a unique constraint |

Each one fits a different question. Buyers want *any* N seats, so they should
never wait for each other. A resize needs the whole event still, so it takes one
lock and holds it. An idempotency key has no row to lock yet, so the key itself is
locked instead.

The advisory lock is the transaction-scoped kind, which Postgres releases on
commit or rollback. The session kind has to be released by hand, and a missed
release stays on the pooled connection and breaks every later request that
borrows it.

### Four places things get checked

| Layer | Handles | Mechanism |
|---|---|---|
| Request shape | required, length, range, format | DataAnnotations |
| Cross-field | allocations sum to capacity, unique tier names, future start date | `IValidatableObject` |
| State | tier exists, event published, inventory sufficient | Services, inside the transaction |
| Storage backstop | illegal states | CHECK constraints |

The last layer matters most. `ck_tickets_sold_has_purchase` means a ticket marked
`Sold` must name the purchase that bought it — the database refuses the row
otherwise, so a half-finished sale cannot be stored even if the code has a bug.

### Custom exceptions

Services throw when something is wrong. One `IExceptionHandler` turns each into
an HTTP status, so no controller catches anything.

| Exception | Status |
|---|---|
| `EventNotFoundException` | 404 |
| `PricingTierNotFoundException` | 404 |
| `PurchaseNotFoundException` | 404 |
| `InsufficientInventoryException` | 409 |
| `CapacityViolationException` | 409 |
| `InvalidEventStateException` | 409 |
| `IdempotencyKeyConflictException` | 422 |
| validation failure | 400 |
| anything else | 500 |

They carry what the caller needs. `InsufficientInventoryException` holds how many
were asked for and how many are left, so the response says "you asked for 20,
there are 5" rather than "sold out".

Responses use ProblemDetails. We do not invent `type` URIs: ASP.NET Core already
supplies one per status, and a made-up URI that resolves to nothing is worse than
the standard one. The two 409s are told apart by their `title` and by the extra
fields they carry.

Anything unmapped returns a generic message and logs the real one. Tested with a
password planted in an exception message — absent from the response, present once
in the log.

### Deliberate omissions

| Not used | Why |
|---|---|
| MediatR / CQRS framework | Read/write split achieved with LINQ projections; no dispatcher needed at this size |
| Repositories | `DbSet<T>` is already one. Two statements have no LINQ equivalent — the `SKIP LOCKED` sale and the bulk seat insert — and EF runs raw SQL directly, so they sit in the services that use them |
| AutoMapper | Hand-written mapping. Compile-time safe and greppable |
| FluentValidation | DataAnnotations + `IValidatableObject` cover shape and cross-field, and are built in |
| FluentAssertions | v8 moved to a paid licence for commercial use, and this repo is public. xunit's `Assert` costs nothing |

### Smaller calls

- **`uuid` v7 keys, generated app-side.** Time-ordered, so bulk seeding fills the
  index sequentially instead of fragmenting it. Ids are known before any round
  trip, so the whole object graph is built in memory before the transaction
  opens. `tickets` is the one table where `bigint` would genuinely be cheaper;
  kept `uuid` for uniformity, and the sale path orders by `seat_ordinal` so PK
  ordering never matters on the hot path.
- **Statuses are `text` + CHECK, not native enums.** Slightly larger rows, far
  easier schema evolution, and maps cleanly to `HasConversion<string>()`.
- **Bulk seeding is one statement per tier**, using
  `unnest(@ids) WITH ORDINALITY`. EF `AddRange` on 50k entities takes seconds and
  overwhelms the change tracker.
- **Health is split live / ready.** Liveness checks nothing external, so a
  database blip never causes an orchestrator to restart a healthy process.
  Readiness runs the database check and returns 503 when it fails.
- **Routes are not prefixed with `/api`.** The host already scopes the service.
- **Attributes on entities, fluent API only for what attributes cannot express** —
  CHECK constraints, partial and descending indexes, SQL defaults, enum
  conversion, delete behaviour.

### Reads project, writes take inputs

Read endpoints project straight off `IQueryable` into types in `Projections/`
— no entity materialisation, no second store, no dispatcher. `RequestFingerprint`
cannot leak into a response because it is never selected. Availability is counted
from inventory rather than read from a stored counter, and the sales report is two
queries at two grains rather than one result set mixing aggregate levels.

Writes take types from `Inputs/`. They are parameter objects, deliberately
**not** named `Command` — there is no command bus, no handlers and no dispatcher,
and borrowing that vocabulary would advertise a pattern this codebase does not use.
`Inputs/` beside `Projections/` makes the direction of each type obvious from the
folder name.

Api request models carry the DataAnnotations and `IValidatableObject` rules and are
mapped to inputs by hand, ~60 lines. The alternative — annotating the inputs and
binding to them directly — deletes that layer but puts the wire contract and the
service input in the same type. Kept separate so they can diverge.

`SaveEventRequest` is shared by `POST` and `PUT`, because the two payloads are
structurally identical and two identical classes read worse than one.

---

## Testing

Integration tests: Testcontainers PostgreSQL, one container per run, migrated
once, Respawn truncation between tests, empty schema per test. Each test builds
what it needs, so no test depends on another's data.

Transaction-per-test rollback is not usable here: the purchase path opens its own
transaction and nesting would change the semantics under test.

The suite leans integration-heavy on purpose. The behaviour that matters is
database behaviour — row locks, bulk seeding, cascade deletes, constraint
enforcement. Substituting `DbContext` to unit-test `EventService` would assert
against a mock's shape rather than against PostgreSQL, and would have caught
neither of the real bugs found while building this.

Unit tests cover the parts that are pure — static methods on the two services that
take everything they need as arguments and touch no database. Everything else in
Services needs PostgreSQL:

| | Covers |
|---|---|
| `TicketPurchaseService.ComputeFingerprint` | item order, email case and whitespace, which fields participate, and that the output is 64 hex characters — a fingerprint that did not fit `char(64)` would fail mid-purchase |
| `TicketPurchaseService.ApplyPricing` | totals across tiers, unit price frozen against a later repricing, `item_total = unit_price * quantity` on every line, decimal precision at `numeric(19,4)`, mixed currencies rejected |
| `EventService` capacity guards | allocations summing to capacity, the allocation-equals-sold boundary, and refusing to shrink or remove a tier with sales |

Making them `public static` was the point. As instance code they could only be
reached with a `DbContext` and an open transaction attached; as static methods
taking their inputs as arguments, they are ordinary functions to test.

These were mutation-checked too. Changing `total += itemTotal` to `total =` and
`allocation < sold` to `allocation < 0` fails four tests across both suites.

**The headline test** is 200 concurrent buyers against a 100-seat event,
released from a shared latch so they genuinely contend:

```
succeeded == 100                    failed == 100
sold == 100                         available == 0
orphaned (Sold, null purchase) == 0
SUM(purchase_items.quantity) == COUNT(sold)
distinct sold ticket ids == sold count
```

The last four are reconciliation checks. A plain count of 100 could be reached
with one orphaned row and one missing one.

**The second** is 20 concurrent requests carrying the same idempotency key: one
`201`, nineteen replays, one purchase row, two tickets sold.

Both were verified by mutation, because a concurrency test that has never been
seen to fail proves nothing:

| Removed | Result |
|---|---|
| `FOR UPDATE SKIP LOCKED` | `Expected: 100, Actual: 200` — every seat sold twice |
| `pg_advisory_xact_lock` | `23505 duplicate key ... uq_purchases_idempotency_key` — 20 callers all insert |
| `FOR UPDATE` on the event row | `23505 duplicate key ... uq_tickets_tier_ordinal` — concurrent grows seed the same range |

In each case the line was restored and the suite re-run twice to confirm it
passes consistently rather than intermittently.

---

## Evolving it

**Scale.** All three concurrency mechanisms are correct and fast to a few
thousand purchases per second on one PostgreSQL instance. Beyond that: read
replicas for availability and reporting (they tolerate staleness; the sale path
must not), then partitioning `tickets` by `event_id`, since inventory is only
ever queried within one event.

**Operability.** Structured logging is in place and every problem response
carries a `traceId`. The next additions would be OpenTelemetry traces around the
sale path, and a metric on sale attempts versus successes — a rising failure
ratio is the earliest signal that an event is selling out.

**Outbox.** Once anything downstream needs to know a purchase happened —
confirmation email, analytics — an outbox table written in the same transaction
keeps that notification consistent with the sale.
