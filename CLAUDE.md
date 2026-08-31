# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Read the README first

`README.md` is the design document, not a quickstart. It explains *why* the concurrency
mechanisms are what they are (one row per seat, `SKIP LOCKED`, the lock matrix, the four
validation layers, the deliberate omissions of MediatR/repositories/AutoMapper). Before
changing anything in the sale path, resizing, or idempotency, read the relevant section —
those decisions have documented alternatives that were considered and rejected.

Treat the README as authoritative on intent, but verify specifics against the code: it is
maintained by hand and can lag. When you change a locking mechanism, a validation layer, or
the shape of the test suite, update it in the same pass.

## Commands

```bash
# Database (Podman or Docker)
podman compose -f LocalInfra/docker-compose.yml up -d

# Schema
dotnet tool restore
dotnet ef database update \
  --project Ripple.Treasury.Assessment.Infrastructure \
  --startup-project Ripple.Treasury.Assessment.Api

# Add a migration
dotnet ef migrations add <Name> \
  --project Ripple.Treasury.Assessment.Infrastructure \
  --startup-project Ripple.Treasury.Assessment.Api

# Run (http://localhost:5205, Scalar reference at /scalar)
dotnet run --project Ripple.Treasury.Assessment.Api

dotnet build
dotnet test

# One project, one class, one test
dotnet test Ripple.Treasury.Assessment.UnitTests
dotnet test --filter "FullyQualifiedName~EventQueryTests"
dotnet test --filter "FullyQualifiedName~EventQueryTests.List_pages_without_dropping_or_repeating_an_event"
```

Integration tests start a PostgreSQL container via Testcontainers, so a container runtime
must be running; they do not use the compose database.

`dotnet build -v q` hides warnings. Use plain `dotnet build` when you care about them —
an empty `async` method (CS1998) is easy to introduce and easy to miss.

## Architecture

`Api → Services → Infrastructure`, enforced by project references. **The Api never touches
`DbContext`.** That single rule is what the layering is for.

- **Invariants live in the services, inside the transaction that enforces them.** Anything
  requiring a query is a state rule and belongs there — not in `IValidatableObject`, which
  only sees the request. Request models validate shape and cross-field arithmetic; CHECK
  constraints are the storage backstop, not the primary guard.
- **Reads project straight off `IQueryable`** into `Services/Projections/`; writes take
  `Services/Inputs/`. No entity materialisation on the read path. This is why
  `RequestFingerprint` cannot leak into a response.
- **Pure logic is `public static` on the services** (`ComputeFingerprint`, `ApplyPricing`,
  the capacity guards) specifically so it unit tests without a database. Keep it that way
  when adding logic that does not need a query.
- **Two statements have no LINQ equivalent** and are raw SQL inside the service that uses
  them: the `SKIP LOCKED` sale and the bulk seat insert (`unnest(...)`).

### Locks

Four mechanisms, four different problems. Do not collapse them.

| Protecting | How | Where |
|---|---|---|
| Selling tickets | `FOR UPDATE SKIP LOCKED` on ticket rows | `TicketPurchaseService.SellSql` |
| Editing an event — repricing / resizing / cancelling | `FOR UPDATE` on the event row | `EventService.LockEventAsync` |
| A sale against a concurrent edit | `FOR SHARE` on the event row | `TicketPurchaseService.LockEventAsync` |
| Reusing an idempotency key | `pg_advisory_xact_lock` + unique constraint | `TicketPurchaseService.PurchaseAsync` |

`FOR SHARE` does not conflict with itself, so concurrent buyers still never block each
other — but it does conflict with the admin's `FOR UPDATE`, which is what stops a sale
completing against an event being edited. Removing it reintroduces that race.

`UpdateAsync` takes that lock at the top, whatever the request changes — do not narrow it
to requests that touch allocations. A sale reads tier prices *after* taking `FOR SHARE`
and `ApplyPricing` copies them onto the purchase row, so an unlocked reprice would charge
whichever price the read happened to catch. The same lock is also why the shrink in
`ResizeTierAsync` can delete ticket rows without `SKIP LOCKED`: a sale can never be in
flight beside it.

The advisory lock must stay the `xact` (transaction-scoped) variant. The session variant
survives on a pooled connection and breaks every later request that borrows it.

## Tests

```
UnitTests/Api/          controllers (NSubstitute on the service interfaces), request
                        validation, request mapping, exception -> ProblemDetails
UnitTests/Services/     the public static pure logic
IntegrationTests/Fixtures/   PostgresFixture, IntegrationCollection
IntegrationTests/Services/   everything needing real PostgreSQL
```

Folders map to the project under test, and namespaces follow the folders. The UnitTests
project references the **Api** project as well as Services — the Api layer is covered by
plain unit tests with NSubstitute, not `WebApplicationFactory`.

Conventions:

- **Shared setup goes in the xUnit constructor**, or `IAsyncLifetime.InitializeAsync` for
  the async half. xUnit builds the class once per test, so constructor state is already
  fresh and isolated; a test may mutate it freely. Do not write a `NewController()` helper
  called from every test. Every integration class resets the schema in `InitializeAsync`,
  so no test repeats it and none can forget it.
- **A concurrency test that has never been seen to fail proves nothing.** When you add or
  change one, revert the mechanism it guards, confirm the test fails, then restore it. The
  README records the expected failure for each existing mechanism. The same applies to any
  test pinning a bug fix.
- `Validator.TryValidateObject` does **not** recurse into list items the way the MVC model
  binder does. Nested `SavePricingTierRequest` / `PurchaseItemRequest` annotations must be
  validated by constructing those objects directly.
- No FluentAssertions — v8 moved to a paid licence for commercial use and this repo is
  public. Use xunit `Assert`.

## Conventions

- **Block-bodied members only.** No expression-bodied (`=>`) members or similarly terse
  modern syntax; classic C# throughout.
- **Entities are plain POCOs** — public get/set, no factories, no private setters.
- **No XML comments in `.csproj`** — add the `PackageReference` / `ProjectReference` alone.
- **Routes carry no `/api` prefix** (`/events`, `/purchases`, `/health`). The host scopes
  the service.
- **Attributes on entities; fluent API only for what attributes cannot express** — CHECK
  constraints, partial and descending indexes, SQL defaults, enum conversion, delete
  behaviour.
- Do not add `Co-Authored-By` or other AI attribution to commit messages.

## Gotchas

- `SuppressAsyncSuffixInActionNames = false` in `Program.cs` is load-bearing: MVC strips the
  `Async` suffix by default, which would break every `nameof(...)` in a `CreatedAtAction`.
- The Api `Dockerfile` must `COPY` all three `.csproj` files before `dotnet restore` —
  restore walks project references, and the Api transitively needs Services and
  Infrastructure. Adding a fourth project means adding a fourth `COPY`.
- An event's pricing tiers must all share one currency (`EventService.ValidateSingleCurrency`).
  The sales report sums a single `TotalRevenue` and reports one `Currency`, so mixed
  currencies would silently add unlike amounts.
- Cancelling a purchase is not implemented. `PurchaseStatus.Cancelled` exists and the sales
  report already excludes such purchases, but nothing sets it. Whatever adds that path must
  also release the seats — `SellSql` only ever selects `status = 'Available'`, so tickets
  left `Sold` on a cancelled purchase leak out of inventory permanently.
