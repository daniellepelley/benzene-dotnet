# Benzene.Outbox.EntityFramework

## What this package does
The relational store for `Benzene.Outbox` (Phase 1): `EntityFrameworkOutboxStore<TDbContext>` (an
`IOutboxStore`) and `EntityFrameworkOutboxStage<TDbContext>` (an `IOutboxStage`), both backed by an
`OutboxRecord` entity mapped into the **application's own** `DbContext` via `ModelBuilder.AddOutboxEntities()`.
This is the sibling to `Benzene.Outbox.DynamoDb`, for the ASP.NET/`Benzene.HostedService` crowd already
running EF Core.

`OutboxWriteMode.Transactional`'s atomic-commit promise (see `Benzene.Outbox/CLAUDE.md`'s "Capability
boundary") is real here because the outbox row lives in the *same* `DbContext` instance as the
handler's own state write: `EntityFrameworkOutboxStage<TDbContext>` adds the row to that `DbContext`'s
change tracker and **never calls `SaveChanges`** — the handler's own `SaveChangesAsync` commits state
+ envelope together, in one database transaction. This is exactly the shape
`docs/cookbooks/transactional-outbox.md` already teaches by hand; this package productizes it.

## No provider package — bring your own
This package references `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational`
only (the latter is the provider-neutral relational conventions layer every relational provider builds
on — `ToTable`, named indexes, `ExecuteUpdateAsync` — not itself a provider). It does **not** reference
SQL Server/PostgreSQL/SQLite/etc. The consuming application registers its own `TDbContext` against
whichever provider it already uses; `AddOutboxEntities()` and this package's types work with any of
them.

## Two DI registrations against the same `TDbContext` — read this before wiring
`AddEntityFrameworkOutbox<TDbContext>` needs **both** of these already registered (or registers
neither for you — see "Usage" below):

- `services.AddDbContext<TDbContext>(options)` — the normal scoped registration your handlers already
  use. `EntityFrameworkOutboxStage<TDbContext>` resolves this exact scoped instance, because sharing it
  with the handler's own `SaveChangesAsync` call *is* the atomicity story.
- `services.AddDbContextFactory<TDbContext>(options)` — with the **same** connection/options.
  `EntityFrameworkOutboxStore<TDbContext>` resolves `IDbContextFactory<TDbContext>` instead of a scoped
  context. This is deliberate, not an oversight: see "Why the store uses a factory" below. Omitting
  this registration surfaces as a DI resolution failure the first time the store is used (claim,
  dispatch, or an `Immediate`-mode capture).

## Why the store uses a factory, not a directly injected scoped `DbContext`
`EntityFrameworkOutboxStage<TDbContext>` legitimately takes a directly injected, scoped
`TDbContext` — it MUST share the handler's instance. `EntityFrameworkOutboxStore<TDbContext>` cannot
do the same thing safely, for a reason rooted in how Phase 1's dispatch engine is built: `AddOutbox`
registers `IOutboxDispatcher` as a **singleton**, constructed once and held for the process's entire
lifetime — every `OutboxDispatcherWorker` poll tick and every `DispatchOneAsync` call (a
stream-triggered relay, say) reuses the *same* `IOutboxStore` instance the dispatcher captured at
construction time. A directly injected scoped `DbContext` handed to that singleton would either be
resolved from an already-disposed scope, or — worse — silently kept alive and mutated across
concurrent calls, and `DbContext` is not thread-safe. `IDbContextFactory<TDbContext>` is itself a
thread-safe, long-lived singleton service designed for exactly this "one long-lived owner, many
short-lived contexts" shape, so `EntityFrameworkOutboxStore<TDbContext>` is registered singleton (same
lifetime as `InMemoryOutboxStore`) and creates-and-disposes a fresh `TDbContext` inside every single
method call. This deliberately deviates from `work/outbox-plan.md`'s Phase 4 wording ("resolves the
scoped `TDbContext`") for the store specifically — the plan's prose undersells the connection-lifecycle
hazard a naively-scoped store would create for a singleton dispatcher; the stage still resolves the
scoped context exactly as written, because that half is correct and required.

## Claim atomicity — and the fallback, honestly
`ClaimAsync`/`ClaimDueAsync` (which claims each of its candidates through `ClaimAsync`) MUST be atomic
per `IOutboxStore`'s contract (`Benzene.Outbox/CLAUDE.md`). The fast path is a single conditioned
`ExecuteUpdateAsync` — an `UPDATE ... WHERE id = @id AND <due-and-unleased>` executed entirely by the
database — so "rows updated" (0 or 1) *is* the atomic claim decision, with no read-then-write race
window. **SQL Server, PostgreSQL, and SQLite all support `ExecuteUpdateAsync`**, so a real deployment
takes this path.

Providers that don't support `ExecuteUpdate` (notably the EF Core **InMemory** provider — this
package's own tests use it, since that's what `test/Benzene.Core.Test` already references, and it
throws `InvalidOperationException` for `ExecuteUpdateAsync`/`ExecuteDeleteAsync`, the same limitation
`Benzene.HealthChecks.EntityFramework`'s tests document for `GetAppliedMigrationsAsync`) fall back to
**optimistic concurrency**: load the row tracked, re-check the same claim predicate in memory, set the
lease, bump a hand-rolled `RowVersion` concurrency token, and `SaveChanges` — a concurrent claimant's
own bumped `RowVersion` makes this `SaveChanges` throw `DbUpdateConcurrencyException`, retried a
bounded number of times before giving up (treated as a refused claim, the same outcome as "someone else
has the lease"). Every other write (`MarkDispatchedAsync`, `RescheduleAsync`, `ParkAsync`) is a plain
load-mutate-`SaveChanges` — not required to be atomic (the dispatcher only calls them on an envelope it
already exclusively holds via a live claim).

## Retention — real deletes, no TTL
Unlike `Benzene.Outbox.DynamoDb` (native TTL, `DeleteDispatchedBeforeAsync` is a no-op),
`EntityFrameworkOutboxStore<TDbContext>.DeleteDispatchedBeforeAsync` performs a real, immediate
`DELETE` of every `Dispatched` row past `OutboxOptions.RetentionPeriod`. `Parked` rows are never
touched by it — see `Benzene.Outbox/CLAUDE.md`'s delivery-semantics note.

## Key types
- `OutboxRecord` — the EF Core row shape: everything `OutboxEnvelope` has (headers serialized as JSON,
  since EF Core has no built-in string-dictionary mapping and this package stays off any one database's
  native JSON column type), plus two store-internal columns `OutboxEnvelope` doesn't carry:
  `LeaseUntil` (the claim lease) and `DispatchedAtUtc` (the retention cutoff), and the `RowVersion`
  concurrency token used only by the optimistic-concurrency fallback claim path.
- `ModelBuilderExtensions.AddOutboxEntities(tableName = "BenzeneOutbox")` — maps `OutboxRecord`, with
  an index on `(Status, NextAttemptAtUtc)` for the sweep query. Call from the application's own
  `OnModelCreating`.
- `EntityFrameworkOutboxStore<TDbContext>` — the `IOutboxStore`. See "Why the store uses a factory" and
  "Claim atomicity" above.
- `EntityFrameworkOutboxStage<TDbContext>` — the `IOutboxStage` for `OutboxWriteMode.Transactional`.
  Adds a row to the caller's own scoped `TDbContext` change tracker; never calls `SaveChanges`.
- `Extensions.AddEntityFrameworkOutbox<TDbContext>(configure?, now?)` — registers the process-wide
  engine (equivalent to calling `AddOutbox(configure)`; safe to call even if you already have, since
  `AddOutbox`'s own `OutboxOptions` registration is idempotent) plus this package's store and stage,
  superseding `AddInMemoryOutboxStore`'s default `IOutboxStore` and `AddOutbox`'s default `IOutboxStage`
  (`BufferedOutboxStage`) if either was already registered.

## Relay host
No new relay code — reuse Phase 1's `OutboxDispatcherWorker` exactly as `Benzene.Outbox/CLAUDE.md`
documents. This package only changes what's underneath `IOutboxStore`/`IOutboxStage`; the worker itself
is host-agnostic and unaware which store is wired in.

## Usage
```csharp
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxEntities();   // "BenzeneOutbox" table, or pass a table name override
        modelBuilder.Entity<Order>();       // ... the application's own entities ...
    }
}

// DI: BOTH registrations against the same options - see "Two DI registrations" above.
services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));
services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));

services
    .AddEntityFrameworkOutbox<AppDbContext>(o => o.WriteMode = OutboxWriteMode.Transactional)
    .AddOutboxDispatcherWorker();   // optional - only if this process also relays

// Outbound routing: opt a route into the outbox, same as Benzene.Outbox/CLAUDE.md's example.
services.AddOutboundRouting(routing => routing
    .Route("payments:capture", pipeline => pipeline
        .UseW3CTraceContext()
        .UseCorrelationId()
        .UseOutbox()
        .UseSqs(queueUrl)));

// Handler: writes state and stages the send through the SAME scoped AppDbContext, then commits both
// with its own SaveChangesAsync. No new "unit of work" type to call through, unlike the DynamoDb
// store - the shared DbContext instance IS the unit of work.
public class CreateOrderHandler
{
    private readonly AppDbContext _dbContext;
    private readonly IBenzeneMessageSender _sender;

    public CreateOrderHandler(AppDbContext dbContext, IBenzeneMessageSender sender)
    {
        _dbContext = dbContext;
        _sender = sender;
    }

    public async Task HandleAsync(CreateOrderRequest request)
    {
        _dbContext.Orders.Add(new Order(request.OrderId, request.CustomerId));

        // UseOutbox() in Transactional mode stages the envelope onto THIS SAME _dbContext's change
        // tracker and returns immediately - nothing is persisted yet.
        await _sender.SendAsync<CapturePaymentRequest, Void>("payments:capture", new(request.OrderId));

        // ONE SaveChangesAsync commits the order row AND the staged outbox row together, in one
        // database transaction. If this throws, neither is persisted - consistent by construction.
        await _dbContext.SaveChangesAsync();
    }
}

// Benzene.HostedService wiring for the relay worker (Benzene.Outbox/CLAUDE.md shows the
// Benzene.SelfHost equivalent) - BenzeneHostedServiceAdapter bridges IBenzeneWorker onto the .NET
// generic host's IHostedService:
services.AddSingleton<IHostedService>(resolver =>
    new BenzeneHostedServiceAdapter(resolver.GetService<IBenzeneWorker>()));
```

## Dependencies on other Benzene packages
- **Benzene.Outbox** — `IOutboxStore`, `IOutboxStage`, `OutboxEnvelope`, `OutboxStatus`,
  `OutboxOptions`, `OutboxWriteMode`, `Extensions.AddOutbox`.
- No dependency on `Benzene.HostedService`/`Benzene.SelfHost` — same "host-agnostic" stance as
  `Benzene.Outbox` itself.

## Conventions
- `IOutboxStore`'s claim methods stay atomic per `Benzene.Outbox/CLAUDE.md`'s hard requirement - see
  "Claim atomicity" above for exactly how (and its documented fallback).
- Time-based logic takes an injectable `Func<DateTimeOffset>` clock
  (`EntityFrameworkOutboxStore<TDbContext>`), matching the house pattern
  (`InMemoryOutboxStore`/`DynamoDbIdempotencyStore`).
- Registration extends `IBenzeneServiceContainer`; `AddEntityFrameworkOutbox<TDbContext>` uses plain
  `AddSingleton`/`AddScoped` (not `TryAdd*`) for the store/stage themselves, so it correctly supersedes
  a previously registered default (`AddInMemoryOutboxStore`, `BufferedOutboxStage`) — the same
  "last-registration-wins, deliberately" pattern `Benzene.Idempotency.DynamoDb.AddDynamoDbIdempotencyStore`
  uses over `Benzene.Idempotency`'s in-memory default.

## Tests
- `test/Benzene.Core.Test/Outbox/EntityFramework/EntityFrameworkOutboxStageTest.cs` — staged rows sit
  on the change tracker unsaved; the handler's own `SaveChangesAsync` commits state + envelope
  together; a scope disposed without `SaveChanges` discards the staged envelope with no leak; multiple
  staged envelopes commit together.
- `test/Benzene.Core.Test/Outbox/EntityFramework/EntityFrameworkOutboxStoreTest.cs` — claim/lease/due
  semantics, reschedule, park, retention cleanup (including that `Parked` rows are never deleted),
  independence across ids, header round-tripping, and — the claim-atomicity requirement — exclusivity
  across two separate `EntityFrameworkOutboxStore` instances (each with its own
  `IDbContextFactory`/`DbContext`) sharing one underlying database, for both `ClaimAsync` and
  `ClaimDueAsync`. Every test in this file exercises the **optimistic-concurrency fallback path**
  specifically, since the test project's only registered provider (EF Core InMemory) doesn't support
  `ExecuteUpdateAsync` — see "Claim atomicity" above.
- `test/Benzene.Core.Test/Outbox/EntityFramework/ModelBuilderExtensionsTest.cs` — default table name,
  the `(Status, NextAttemptAtUtc)` index, and the table name override.
