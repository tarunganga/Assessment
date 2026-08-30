# Event Ticketing API

A RESTful API for a simplified event ticketing system: event management, ticket
purchasing, availability, and sales reporting.

.NET 10 · ASP.NET Core (controllers) · EF Core 10 · PostgreSQL 18

Everything in the requirement is standard CRUD except one line — **prevent
overselling**. That is a rule spanning many rows that has to hold while lots of
people buy at the same time, and the other choices here follow from it.

---

## Setup

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

# Tests. Integration tests bring up their own PostgreSQL, so steps 1 and 2
# are not needed for them.
dotnet test
```

| | |
|---|---|
| API | http://localhost:5205 |
| API reference (Scalar) | http://localhost:5205/scalar |

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
    *Service.cs           what the app does, the rules it enforces, and where
                          transactions start and end. The pure parts - pricing,
                          capacity, fingerprint - are static methods, so they
                          unit test without a database
    Inputs/               what a write needs
    Projections/          what a read returns
    Exceptions/           failures the Api turns into status codes

Ripple.Treasury.Assessment.Infrastructure
    Entities/             POCOs with mapping attributes
    Enums/
    Migrations/
    TicketingDbContext    fluent config for what attributes cannot say

Ripple.Treasury.Assessment.UnitTests
    Api/                  controllers, request validation, mapping, error mapping
    Services/             the pure static logic

Ripple.Treasury.Assessment.IntegrationTests
    Fixtures/             Testcontainers PostgreSQL, Respawn reset
    Services/             everything that needs a real database

LocalInfra/                                   docker-compose for PostgreSQL
```

Test folders mirror the project they test, and namespaces follow the folders.

Everything points one way: `Api → Services → Infrastructure`, and the project
references make that so. The Api never touches `DbContext`. That one rule is what
makes the Services layer worth having.

---

## API

| | | |
|---|---|---|
| `POST` | `/events` | 201, creates the event with its tiers and its seats |
| `GET` | `/events?from=&venue=&page=&pageSize=` | 200, paged list |
| `GET` | `/events/{id}` | 200, with pricing tiers |
| `PUT` | `/events/{id}` | 200, resizes seats; 409 if a tier would drop below what is sold |
| `DELETE` | `/events/{id}` | 204, deletes if nothing sold, otherwise marks it `Cancelled` |
| `POST` | `/events/{id}/publish` | 200, `Draft` to `Published` |
| `GET` | `/events/{id}/availability` | 200, seats left per tier, counted from the tickets |
| `POST` | `/events/{id}/purchases` | 201 or 200, needs an `Idempotency-Key` header |
| `GET` | `/events/{id}/sales-report` | 200, totals and a per-tier breakdown |
| `GET` | `/purchases/{id}` | 200, items and ticket ids |
| `GET` | `/health/live`, `/health/ready` | 200, or 503 when the database is down |

Routes are not prefixed with `/api` — the host already puts the service under its
own path.

### Buying twice by accident

`POST /events/{id}/purchases` needs an `Idempotency-Key` header. A client that
times out and retries must not end up with two sets of tickets.

| Case | Response |
|---|---|
| New key | `201 Created` |
| Same key, same request | `200 OK` with `Idempotent-Replay: true` |
| Same key, different request | `422 Unprocessable Entity` |

A SHA-256 fingerprint of the request decides which case it is. The request is
tidied up first — item order and email casing are ignored — so a genuine retry
matches even if it is not byte-for-byte the same.

---

## The data model

Five tables, at three levels of detail.

| Table | Purpose |
|---|---|
| `events` | The show: name, venue, when, total capacity, status |
| `pricing_tiers` | A named price band inside an event, with its price and its share of the capacity |
| `tickets` | The actual stock. One row per seat we can sell — the rows `SKIP LOCKED` sells from |
| `purchases` | The order. One row per purchase, holding the idempotency key and the total |
| `purchase_items` | What was bought per tier, at the price as it was that day |

`purchases` says an order happened, `purchase_items` says what was agreed and for
how much, `tickets` says which seats went out.

`purchase_items.quantity` could be worked out by counting tickets, and we store it
anyway. That is on purpose: `SUM(purchase_items.quantity)` has to match
`COUNT(tickets WHERE status = 'Sold')` for an event, so we have the same fact
recorded twice by two different paths and can check one against the other.
`unit_price` is different — it **cannot** be worked out later, because tier prices
change and past revenue must not move when an admin edits a price. That column is
why the table exists.

Every tier on one event has to use the same currency. The sales report adds up one
`TotalRevenue` and shows one `Currency`, so mixing currencies would add pounds to
dollars; `EventService.ValidateSingleCurrency` blocks it on create and update.

---

## Design decisions and trade-offs

### Why a relational database

The rule that shapes everything: a tier must never sell more tickets than it has
seats. Checking that means counting rows, and the count has to be right at the
exact moment two people are buying.

| Also considered | Why not |
|---|---|
| Document store (MongoDB, DynamoDB) | These promise that one document changes all-or-nothing, so the seat count has to sit inside a single document per tier — and then every buyer for that tier queues behind that one document. A purchase here also writes three tables together, which is awkward when only one document at a time is safe |
| Event store / append-only log | Good at recording what happened, which suits money. But to answer "is this seat free" you have to build a separate copy of the data, and that copy is always a little behind. That gap is where an oversell slips through |

The shape is relational: an event has tiers, a tier has tickets, a purchase has
items, and the database itself can make sure a ticket always points at a tier
that exists. Separately, a purchase needs ACID — it writes `purchases`,
`purchase_items` and `tickets`, and they have to land together or not at all. The
sales report is grouping and adding up, which is what SQL is for.

### Where overselling is stopped

In the database, not in the API.

The database is the only part of the system that sees every buyer.

| Also considered | Why not |
|---|---|
| A lock inside the app (`SemaphoreSlim`, `lock`) | Fine with one copy of the API running. Breaks as soon as you run two, because each copy has its own lock and neither knows about the other. Worst kind of bug: passes every test on your machine, then oversells in production |
| Put purchases on a queue, one worker per event | This does work, and it is a real pattern. But one worker per event means one sale at a time again, and now there is a queue to run and message ordering to get right |

Even if the API guarded it, the database would still have to. A retry, a script
someone runs by hand, or a second service can all write to the tables without
going past the API's lock. Since the database has to enforce the rule anyway,
doing it only there means there is one answer to "how many are sold" instead of
two that can disagree.

### Which kind of lock

| Approach | What happens when two people buy at once | |
|---|---|---|
| Optimistic — let both write, spot the clash, retry | Works, but the busier the event the more everyone retries. Worst exactly when the show is selling out | not used |
| `SERIALIZABLE` isolation — let Postgres spot the clashes | Works, but it is optimistic underneath, so the same retry problem, and it watches everything the transaction reads, not just seats | not used |
| Lock the whole tier or event row | Works and is simple, but everyone buying that tier waits in line. How fast you can sell becomes how fast one purchase finishes | not for sales, **used** for resizing |
| Lock only the rows being taken (`FOR UPDATE SKIP LOCKED`) | Nobody waits. Each buyer takes seats nobody else is holding and steps over the rest | **used** |
| Distributed lock (Redis, etcd) | Something outside the database decides who goes first | not yet — see below |

Locking individual rows works here because of something true about ticketing, not
about databases: **a buyer wants any N seats, not specific ones.** That is what
makes it safe to step over a row someone else is holding. If people picked exact
seats off a map, stepping over would be wrong — two people both wanting 14A really
are after the same thing — and locking the whole section, or optimistic retry,
would fit better.

Locking a whole row is not a bad idea in general, just for selling. Resizing a
tier does need the whole event to sit still, so that is exactly what it does.
Different job, different lock.

**On distributed locks.** They become the right answer once seats stop living in
one database — split across shards or regions, or with more than one service able
to sell the same seat. At that point no single database sees every sale, and
something outside has to decide who goes first. That is a likely direction for
this system as it grows.

It would not help today. Redis and Postgres cannot commit together, so if the lock
runs out early or the network drops, Redis can tell two callers "go ahead" while
both write. The database check has to be there either way — and while it is there,
and the database still sees every sale, the lock is not adding anything.

### One row per seat, not a counter

Creating an event creates a `tickets` row for every seat it will sell.

The obvious alternative is a counter — one number per tier, bumped on each sale.
It is cheaper and it works. We keep rows instead for three reasons:

- **`SKIP LOCKED` needs rows.** Two buyers can take different seats at the same
  time because each locks the rows it is taking and steps over the ones the other
  holds. With a single counter there is nothing to step over; everyone queues.
- **A counter can go wrong quietly.** If it ever disagrees with what was actually
  sold, there is nothing to compare it against. Rows can be counted and checked
  against `purchase_items`, which is what the oversell test does.
- **Rows can be pointed at one by one.** A seat can be assigned, transferred or
  refunded on its own. A counter only knows how many.

The cost is real: a 50,000-seat event writes 50,000 rows up front, about 11 MB, in
roughly 700 ms. That happens even for an event nobody publishes.

**Also considered:** creating the rows on publish rather than on create. It saves
that write for drafts nobody finishes, but then a tier either has rows or does
not, and both update and publish have to handle each case.

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

`SKIP LOCKED` does the work. A buyer takes the first N seats nobody else is
holding and walks past the taken ones instead of waiting for them. Two people
buying at once get different seats and neither is held up.

Getting back fewer rows than you asked for means the tier ran out. There is no
count to check first, and no gap between checking and taking — the rows come back
already sold.

`ix_tickets_available` indexes only the unsold rows, in seat order, so the query
reads them straight off the index without sorting. At 50,000 seats with 45,000
sold: **3 page reads, 0.041 ms.**

### Resizing a tier: one at a time per event

Resizing a tier touches a lot of rows at once — counting what is sold, adding
seats, removing unsold ones — so `EventService` locks the event row first and
holds it until the change is done. One resize at a time per event.

Without it, two people growing the same tier both read the same highest seat
number and both try to add seats from there. There is a test for this: take the
lock out and it fails with `23505 duplicate key value violates unique constraint
"uq_tickets_tier_ordinal"`.

### Four kinds of lock, four different problems

| What is being protected | How |
|---|---|
| Selling tickets | `FOR UPDATE SKIP LOCKED` on the ticket rows |
| Resizing or cancelling an event | `FOR UPDATE` on the event row |
| A sale racing a cancellation | `FOR SHARE` on the event row |
| Reusing an idempotency key | `pg_advisory_xact_lock` on the key, plus a unique constraint |

Each answers a different question. Buyers want *any* N seats, so they should never
wait for each other. A resize needs the whole event to sit still, so it takes one
lock and holds it. An idempotency key has no row to lock yet, so the key itself is
locked instead.

The third goes with the second. A purchase checks the event is `Published` before
selling, and that check is worth nothing if an admin can cancel the event in the
gap between the check and the sale. `FOR SHARE` does not clash with itself, so
buyers still never block each other, but it does clash with the `FOR UPDATE` that
cancelling takes — so the two cannot slip past each other. Whichever finishes
first, the other one sees it.

The advisory lock is the kind tied to the transaction, which Postgres lets go on
commit or rollback. The other kind is tied to the session and has to be released
by hand; miss one and it stays on the pooled connection, breaking every later
request that borrows it.

### Four places things get checked

| Layer | Handles | How |
|---|---|---|
| Request shape | required, length, range, format | DataAnnotations |
| Across fields | allocations add up to capacity, tier names unique, one currency, start date in the future | `IValidatableObject` |
| Current state | tier exists, event published, enough seats left | Services, inside the transaction |
| Last line of defence | states that should never exist | CHECK constraints |

Anything that needs a query belongs in the services, inside the transaction that
enforces it. Checking before the transaction opens leaves a gap where the answer
can change before you act on it.

The last layer matters most. `ck_tickets_sold_has_purchase` means a ticket marked
`Sold` has to name the purchase that bought it — the database refuses the row
otherwise, so a half-finished sale cannot be stored even if the code has a bug.

### Custom exceptions

Services throw when something is wrong. One `IExceptionHandler` turns each into an
HTTP status, so no controller catches anything.

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
were asked for and how many are left, so the response can say "you asked for 20,
there are 5" rather than "sold out".

Responses use ProblemDetails. We do not make up `type` URIs: ASP.NET Core already
supplies one per status, and a made-up URI that leads nowhere is worse than the
standard one. The two 409s are told apart by their `title` and the extra fields
they carry.

Anything not on the list returns a generic message and logs the real one. Tested
by planting a password in an exception message — it is absent from the response
and present once in the log.

### Left out on purpose

| Not used | Why |
|---|---|
| MediatR / CQRS framework | Reads and writes are already split by using different types; nothing here needs a dispatcher |
| Repositories | `DbSet<T>` is already one. Two statements have no LINQ equivalent — the `SKIP LOCKED` sale and the bulk seat insert — and EF runs raw SQL directly, so they sit in the services that use them |
| AutoMapper | Mapping is written by hand. The compiler catches mistakes and you can grep for it |
| FluentValidation | DataAnnotations and `IValidatableObject` cover shape and across-field rules, and are built in |
| FluentAssertions | v8 moved to a paid licence for commercial use, and this repo is public. xunit's `Assert` costs nothing |

### Smaller calls

- **`uuid` v7 keys, made in the app.** They sort by time, so inserting 50,000 at
  once fills the index in order instead of scattering writes through it. The ids
  are known before we talk to the database, so the whole object graph is built in
  memory before the transaction opens. `tickets` is the one table where `bigint`
  would genuinely be cheaper; kept `uuid` to stay consistent, and the sale path
  sorts by `seat_ordinal`, so the key order never matters where it counts.
- **Statuses are `text` + CHECK, not database enums.** Slightly bigger rows, much
  easier to change later, and maps cleanly to `HasConversion<string>()`.
- **Seats are seeded one statement per tier**, using `unnest(@ids) WITH
  ORDINALITY`. EF `AddRange` on 50k entities takes seconds and swamps the change
  tracker.
- **Health is split live / ready.** Liveness checks nothing outside the process,
  so a database hiccup never makes the platform restart a process that is fine.
  Readiness does check the database and returns 503 when it is down.
- **Attributes on entities, fluent API only for what attributes cannot say** —
  CHECK constraints, partial and descending indexes, SQL defaults, enum
  conversion, delete behaviour.
- **`SuppressAsyncSuffixInActionNames = false`.** MVC drops the `Async` suffix
  from action names by default, which would break every `nameof(...)` in a
  `CreatedAtAction`.

### Reads return projections, writes take inputs

Reads go straight from the query into the small types in `Projections/` — the
entities are never loaded, there is no second copy of the data, and no dispatcher.
`RequestFingerprint` cannot leak into a response because it is never selected.
Availability is counted from the tickets rather than read from a stored number,
and the sales report is two queries at two levels rather than one result set
mixing them.

Writes take types from `Inputs/`. They are just bags of parameters, and are
deliberately **not** called `Command`: there is no command bus, no handlers, no
dispatcher. `Inputs/` next to `Projections/` makes the direction of each type
obvious from the folder name.

The Api request models hold the DataAnnotations and `IValidatableObject` rules and
are mapped to inputs by hand, about 60 lines. The alternative — putting the
annotations on the inputs and binding to them directly — removes that layer but
puts the shape of the request and the service input in the same class. Kept apart
so they can drift without dragging each other along.

`SaveEventRequest` is shared by `POST` and `PUT`, because the two bodies are the
same shape and two identical classes read worse than one.

---

## Testing

Testcontainers PostgreSQL, one container per run, migrated once, and Respawn
empties the tables in `IAsyncLifetime.InitializeAsync` so every test starts clean
and sets up what it needs. Wrapping each test in a transaction and rolling back is
not an option here: the purchase path opens its own transaction, and nesting one
inside another would change the very thing under test.

### What is tested where

The Services layer is tested against real PostgreSQL, because what matters there
is what the database does — row locks, bulk inserts, cascade deletes, constraints.
Faking `DbContext` to unit test `EventService` would only check that we called the
fake the way we expected, and would have caught none of the real bugs found while
building this.

Its pure parts are the exception. They are `public static` methods that take
everything they need as arguments and never touch the database, which is what
makes them ordinary functions to test:

| | Covers |
|---|---|
| `TicketPurchaseService.ComputeFingerprint` | item order, email case and spaces, which fields count, and that the output is 64 hex characters — a fingerprint too long for `char(64)` would blow up mid-purchase |
| `TicketPurchaseService.ApplyPricing` | totals across tiers, unit price held against a later price change, `item_total = unit_price * quantity` on every line, decimals at `numeric(19,4)`, mixed currencies rejected |
| `EventService` guards | allocations adding up to capacity, the allocation-equals-sold boundary, refusing to shrink or remove a tier with sales, one currency per event |

The Api layer needs no database at all, so it is unit tested throughout, with
NSubstitute standing in for `IEventService` and `ITicketPurchaseService`:

| | Covers |
|---|---|
| Controllers | what each action returns and where it points, that create and update read the event back instead of echoing the request, page and page-size clamping, and the `Idempotency-Key` path — missing key rejected before the service is called, first purchase `201`, replay `200` with the header |
| Request validation | the DataAnnotations and both `IValidatableObject` implementations, including that every broken rule comes back in one go |
| Request mapping | field for field, that the create and update mappings do not drift apart, and that the tier list is copied rather than shared |
| Error mapping | every exception to its status, that an unknown exception leaks nothing, and a guard that no domain exception quietly falls through to 500 |

`Validator.TryValidateObject` does not walk into list items the way the MVC model
binder does, so rules on nested tiers and items are tested by building those
objects directly.

### Concurrency

`Two_hundred_concurrent_buyers_cannot_oversell_a_hundred_seats` is the headline:
200 buyers against a 100-seat event, all let go at the same moment so they really
do collide.

```
succeeded == 100                    failed == 100
sold == 100                         available == 0
orphaned (Sold, null purchase) == 0
SUM(purchase_items.quantity) == COUNT(sold)
distinct sold ticket ids == sold count
```

The last four are cross-checks. A plain count of 100 could be reached with one
stray row and one missing one.

The rest:

| Test | Checks |
|---|---|
| `Twenty_concurrent_identical_requests_produce_one_purchase` | one `201`, nineteen replays, one purchase row, two tickets sold |
| `Concurrent_updates_are_serialised_by_the_event_row_lock` | two tier grows at once both succeed, with 90 different seat numbers rather than a clash |
| `A_purchase_waits_for_an_in_flight_cancellation_and_then_sees_it` | the purchase waits while the cancellation holds the event row, then fails once it goes through |
| `A_cancellation_waits_for_an_in_flight_purchase` | the other way round: the delete waits behind a buyer holding `FOR SHARE` |
| `Concurrent_buyers_do_not_block_each_other_on_the_event_row` | the event lock still does not put ordinary sales in a queue |

A concurrency test that has never been seen to fail proves nothing, so each one
was checked by taking out the thing it guards:

| Taken out | Result |
|---|---|
| `FOR UPDATE SKIP LOCKED` | `Expected: 100, Actual: 200` — every seat sold twice |
| `pg_advisory_xact_lock` | `23505 duplicate key ... uq_purchases_idempotency_key` — all 20 callers insert |
| `FOR UPDATE` on the event row | `23505 duplicate key ... uq_tickets_tier_ordinal` — two grows seed the same seat numbers |
| `FOR SHARE` on the event row | the purchase goes straight through, selling against an event being cancelled |

Each time the line was put back and the suite run twice, to be sure it passes
every time rather than most of the time. Same for the pure logic: changing
`total += itemTotal` to `total =` and `allocation < sold` to `allocation < 0`
fails four tests across both suites.
