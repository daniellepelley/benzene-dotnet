# Transactional Outbox

Publish a handler's outbound sends **durably**, and — with the right store — **atomically with the
handler's own state write**, so a crash between "commit the order" and "send `order:placed`" can
never silently lose (or duplicate-without-record) the send.

`Benzene.Outbox` ships this as first-class packages: `Benzene.Outbox` (the host-agnostic engine),
`Benzene.Outbox.DynamoDb` and `Benzene.Outbox.EntityFramework` (the stores that make the atomic
write mode real). This cookbook is the guide to using them.

## Problem Statement

A handler that must "write state AND send a message" today does two independent operations: it
commits to its own store, then calls `IBenzeneMessageSender.SendAsync` to publish. If the process
dies (or the transport is unreachable) between the two, you've committed the state but never sent
the message — or sent it and then rolled the state back. The common workaround, wrapping the send
in a swallow-and-log `try`/`catch`, just trades a crash for silent data loss.

The outbox pattern removes the dual write: capturing a send writes an *envelope* durably — in the
best case, in the same transaction as the state write — and a separate **relay** dispatches it
afterwards. The relay gives you at-least-once delivery instead of best-effort.

## Prerequisites

- A reference to `Benzene.Outbox` (the engine), plus:
  - `Benzene.Outbox.DynamoDb` if you're on DynamoDB and want the atomic write mode, or
  - `Benzene.Outbox.EntityFramework` if you're on EF Core and want the atomic write mode, or
  - neither, if store-and-forward durability alone is enough (see [the two write
    modes](#the-two-write-modes--be-honest-about-what-each-guarantees) below).
- An outbound route for the topic you want to outbox (`AddOutboundRouting` — see [Clients](../clients.md)).
- The handler-facing API doesn't change: it's still `IBenzeneMessageSender.SendAsync<TRequest,
  Void>(topic, request)`. Outboxing is opt-in per route, in the route's own pipeline — nothing about
  the call site changes.

## Step 1 — register the engine and a store

```csharp
services
    .AddOutbox(o => o.MaxAttempts = 10)   // the dispatch engine's shared options
    .AddInMemoryOutboxStore()             // single-process only — see "Choosing a store" below
    .AddOutboxDispatcherWorker();         // optional — only if this process also relays (see "Relay per host")
```

`AddInMemoryOutboxStore` is fine for a single instance or a demo, but envelopes captured on one
instance are only ever dispatched by that same instance's worker. For a multi-instance or Lambda
deployment, swap in a real store:

```csharp
// DynamoDB
services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
services
    .AddOutbox(o => o.WriteMode = OutboxWriteMode.Transactional)
    .AddDynamoDbOutboxStore("orders-outbox")
    .AddDynamoDbOutboxTransaction("orders-outbox");
```

```csharp
// EF Core — see Step 4 for the full picture, including the DbContext/DbContextFactory pair it needs.
services.AddEntityFrameworkOutbox<AppDbContext>(o => o.WriteMode = OutboxWriteMode.Transactional);
```

Neither store package creates its own table — provision the DynamoDB table (+ TTL + sparse GSI) or
the EF migration yourself; see each package's `CLAUDE.md` for the exact shape.

## Step 2 — add `UseOutbox()` to the route

`UseOutbox()` is outbound-route middleware, not a new send API. Add it after the cross-cutting
stamping middleware and before the terminal transport converter, so the captured envelope carries
the business-time `traceparent`/`x-correlation-id`:

```csharp
services.AddOutboundRouting(routing => routing
    .Route("payments:capture", pipeline => pipeline
        .UseW3CTraceContext()
        .UseCorrelationId()
        .UseOutbox()                 // <-- capture point
        .UseSqs(queueUrl)));
```

That route now captures instead of sending inline. Handler code is unchanged:

```csharp
await sender.SendAsync<CapturePaymentRequest, Void>("payments:capture", request);
```

**Constraint:** an outboxed route is fire-and-forget only. `SendAsync<TRequest, TResponse>` with any
`TResponse` other than `Void` throws the existing `OutboundResponseTypeMismatchException` — the same
behavior a send-acknowledgement-only transport (SQS/SNS/...) already produces. Request/response
topics can't be outboxed; a deferred send has no response to give back.

## The two write modes — be honest about what each guarantees

Neither mode is exactly-once. Pick the one your route actually needs.

- **`OutboxWriteMode.Immediate` (the default): store-and-forward.** Capture writes the envelope
  straight to the store. This guarantees the send survives process death and transport outages, is
  retried with backoff, and is never silently swallowed — it replaces the try/catch pattern
  outright. It does **not** make the send atomic with the handler's own state write: the envelope
  write and the state write are still two independent operations, just both now durable (rather
  than one durable and one best-effort).
- **`OutboxWriteMode.Transactional`: the actual atomic story — but it needs a store package.**
  Capture stages the envelope in a scoped buffer instead of writing it. Nothing in `Benzene.Outbox`
  on its own ever drains that buffer into storage — that's the job of an **outbox-aware unit of
  work** from a store package (Step 3 and Step 4, below). If you set `WriteMode = Transactional`
  but never install one of those (or your own unit of work), staged envelopes are silently
  discarded when the scope disposes — the write never happens. That's consistent by construction
  (no state was written either), but it means `Transactional` mode with no store package installed
  is a misconfiguration, not a smaller guarantee.

Set the mode process-wide via `AddOutbox(o => o.WriteMode = ...)`, or per route by passing the same
option to `UseOutbox(o => o.WriteMode = ...)` — a route-level override clones the shared options, so
one route can diverge without affecting any other.

## Step 3 — the DynamoDB unit of work

`Benzene.Outbox.DynamoDb` makes `Transactional` mode real for DynamoDB: the handler commits through
`IDynamoDbOutboxTransaction`, which drains everything staged on the request's scope and issues
**one** `TransactWriteItems` containing the application's own items plus a `Put` per staged
envelope. All-or-nothing — either the state and every envelope persist, or none of them do.

```csharp
public async Task<IBenzeneResult<Void>> Handle(CreateOrderRequest request, IDynamoDbOutboxTransaction outbox)
{
    await _sender.SendAsync<CapturePaymentRequest, Void>("payments:capture", ToCaptureRequest(request));
    // ^ captured, not sent yet — staged on this scope's buffer.

    var orderPut = new TransactWriteItem { Put = new Put { TableName = "orders", Item = ToItem(request) } };
    await outbox.CommitAsync([orderPut]);   // ONE TransactWriteItems: order + captured envelope(s).

    return BenzeneResult.Created<Void>();
}
```

Bounded by DynamoDB's 100-item transaction limit, shared with the application's own items —
`CommitAsync` throws a clear `InvalidOperationException` if the combined count would exceed it, or
if you call it with nothing staged and no application items (a caller-side no-op is surfaced loudly,
not silently swallowed).

## Step 4 — the EF Core same-DbContext flow

`Benzene.Outbox.EntityFramework` makes `Transactional` mode real for EF Core by putting the outbox
row in the **application's own `DbContext`**. `EntityFrameworkOutboxStage<TDbContext>` adds the row
to that `DbContext`'s change tracker and **never calls `SaveChanges`** — the handler's own
`SaveChangesAsync` is the commit. There's no separate "unit of work" type to call through here; the
shared `DbContext` instance *is* the unit of work.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.AddOutboxEntities();   // "BenzeneOutbox" table, or pass a table name override
    modelBuilder.Entity<Order>();
}
```

```csharp
// Both registrations are required — the stage needs the scoped DbContext (to share the handler's
// instance), the store needs a DbContextFactory (it's a singleton behind a per-call fresh context —
// see Benzene.Outbox.EntityFramework/CLAUDE.md's "Why the store uses a factory" for why).
services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));
services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));
services.AddEntityFrameworkOutbox<AppDbContext>(o => o.WriteMode = OutboxWriteMode.Transactional);
```

```csharp
public async Task HandleAsync(CreateOrderRequest request)
{
    _dbContext.Orders.Add(new Order(request.OrderId, request.CustomerId));

    // UseOutbox() in Transactional mode stages the envelope onto THIS SAME _dbContext's change
    // tracker and returns immediately — nothing is persisted yet.
    await _sender.SendAsync<CapturePaymentRequest, Void>("payments:capture", new(request.OrderId));

    // ONE SaveChangesAsync commits the order row AND the staged outbox row together, in one
    // database transaction. If this throws, neither is persisted — consistent by construction.
    await _dbContext.SaveChangesAsync();
}
```

This is exactly the shape this cookbook used to teach you to hand-roll — `Benzene.Outbox.EntityFramework`
productizes it.

## Relay per host

Dispatch is one host-agnostic engine (`IOutboxDispatcher`); only *how it gets invoked* differs by
where the service runs.

- **`Benzene.HostedService` / `Benzene.SelfHost` (anything with a background thread).** Resolve
  `OutboxDispatcherWorker` (registered by `AddOutboxDispatcherWorker()`) and wire it into the host —
  a poll loop (default every 5 seconds) that claims a due batch and dispatches it, with a graceful
  stop that finishes an in-flight run:

  ```csharp
  // Benzene.SelfHost
  app.Workers.Add(resolverFactory =>
  {
      using var scope = resolverFactory.CreateScope();
      return scope.GetService<IBenzeneWorker>();
  });

  // Benzene.HostedService — BenzeneHostedServiceAdapter bridges IBenzeneWorker onto IHostedService
  services.AddSingleton<IHostedService>(resolver =>
      new BenzeneHostedServiceAdapter(resolver.GetService<IBenzeneWorker>()));
  ```

- **AWS Lambda — no background thread, so the poll worker doesn't apply.** Three options were
  weighed; the documented default is a **pair**:
  1. **DynamoDB Streams → dispatch handler** *(recommended primary)*. Enable streams on the outbox
     table; the service's own Lambda consumes `{table}:INSERT` through `Benzene.Aws.Lambda.DynamoDb`
     and calls `IOutboxDispatcher.DispatchOneAsync(envelopeId)` for the inserted row. Near-real-time,
     zero idle cost, no new transport code. Caveat: `INSERT` fires once, so *retries* on a
     persistently failing dispatch need the sweeper below — configure the event source mapping's
     `maximum_retry_attempts` and let a failing shard fall through to it.
  2. **Scheduled sweep** *(required backstop, and the minimal standalone option)*. An EventBridge
     schedule invokes the same Lambda on an **app-chosen topic** (never `benzene:*` — reserved
     topics are spec surface) whose 3-line handler calls `IOutboxDispatcher.RunOnceAsync()`. This is
     what actually handles retries, parking, and retention cleanup — a deployment that can tolerate
     schedule-granularity latency can run sweep-only and skip streams entirely.
  3. **Dispatch-on-invoke piggyback — rejected as a primary relay.** Flushing pending envelopes at
     the end of every invocation was considered and rejected: it couples drain latency to traffic
     (an idle service never drains its backlog), extends every invocation's billed duration, and
     background work after the response is impossible under Lambda's freeze model. It remains a
     possible future *optimization* ("inline first-attempt after commit"), not a relay — not
     implemented today.

  `examples/AwsMesh/Orders` dogfoods exactly the streams-plus-sweep pair end to end, including the
  Terraform for both — see [its README's "The outbox"
  section](../../examples/AwsMesh/README.md#the-outbox-atomic-commit-stream-dispatch-sweep-redrive-dedup-at-the-consumer).

- **Other FaaS (Azure Functions, ...).** Same shape — a change-feed/timer trigger calling into
  `IOutboxDispatcher` — deferred alongside a future Cosmos store.

## Delivery semantics and parking

State these plainly, because they apply regardless of write mode or store:

- **At-least-once, end to end.** Duplicates are possible — a crash after a successful send but
  before it's marked dispatched, or a stream-triggered dispatch racing a sweep over the same
  envelope. There is no exactly-once claim anywhere in this feature.
- **No ordering guarantee across envelopes.** A sweep orders by creation time best-effort only, a
  stream-triggered dispatch has no such ordering at all, and retries reorder regardless. Per-key
  ordered dispatch isn't implemented.
- **Retention.** A dispatched envelope is retained for `OutboxOptions.RetentionPeriod` (default 7
  days) before cleanup removes it — DynamoDB via native TTL on `expiresAt`, EF Core via the sweep's
  own delete (no TTL there). A **parked** envelope is never auto-deleted; it's the operator's
  evidence.
- **Poison envelopes are parked, not dead-lettered.** After `OutboxOptions.MaxAttempts` (default
  10, exponential backoff base 30s capped at 1 hour), an envelope becomes `OutboxStatus.Parked` and
  stops retrying. There is no dead-letter forwarding — parking is the deliberate boundary; re-pending
  or purging a parked envelope is a manual store operation today.

## Pairs with `Benzene.Idempotency`

At-least-once delivery means consumers need dedup. At capture, if the route's headers lack
`idempotency-key`, `UseOutbox()` stamps the envelope's own id into that header by default
(`OutboxOptions.StampIdempotencyKey = true`). A consumer running [`Benzene.Idempotency`](idempotency.md)'s
`UseIdempotency()` with its default key strategy then dedups relay redeliveries with zero extra
configuration — the two packages click together without either referencing the other. If you outbox
a route, put `UseIdempotency()` on the consumer at the other end.

## Response-as-event flows get this for free

If you use [`UseResponseEvents`](response-as-event.md) to republish a handler's response as an
event, you don't need to do anything extra to outbox it: the default `IResponseEventPublisher`
publishes through `IBenzeneMessageSender`, same as any other send, so the moment the event topic's
outbound route adds `UseOutbox()`, response-as-event publishes inherit exactly the same
durability/atomicity story as a handler-initiated send. No custom publisher, no integration code.

## Testing

- **Middleware:** build a route via `AddOutboundRouting` + `UseOutbox()` and a recording terminal
  middleware, then assert a send short-circuits with an `Accepted` result and never reaches the
  terminal step; assert a non-`Void` `SendAsync` throws the mismatch exception.
- **Atomicity (DynamoDB or EF Core):** run the handler through `BenzeneTestHost` against a real
  local table/database, make the handler throw *after* the state write but before the commit, and
  assert neither the state row nor the envelope persisted.
- **Dispatch:** seed a store with a due envelope, call `IOutboxDispatcher.RunOnceAsync()` against a
  fake `IBenzeneMessageSender`, and assert it dispatched with the envelope's original headers
  (including `traceparent`) re-applied.

See `test/Benzene.Core.Test/Outbox/` in this repo for the full worked test suite the packages ship
with.

## Variations & Gotchas

- **Not every route needs the outbox.** Opt in per route — the routes you don't add `UseOutbox()`
  to behave exactly as before. Most services will only outbox the sends that matter enough to be
  durable (or atomic) with a state write; leave the rest as plain sends.
- **Choosing a store.** `InMemoryOutboxStore` is single-process — fine for one instance or a demo,
  wrong for a fleet or Lambda (where every invocation can be a different process). Use
  `Benzene.Outbox.DynamoDb` or `Benzene.Outbox.EntityFramework` for anything that scales past one
  instance.
- **A renamed payload type strands pending envelopes.** The envelope's payload must round-trip
  through the registered `ISerializer` (deserialized to the stored type name at dispatch) — the same
  constraint `Benzene.EventSourcing` places on stored events. Dispatch runs in the same service, so
  the type is normally always loadable; a breaking rename before every pending envelope drains will
  strand them (they'll retry, fail, and eventually park).
- **Cosmos DB is deferred.** `TransactionalBatch` only spans one container *and* one partition key,
  so an outbox item would have to share the application document's partition — real, but
  constraint-laden. Not shipped yet.

## Related

- [Response as Event](response-as-event.md) — the flows that inherit the outbox for free once their
  topic's route opts in.
- [Idempotency](idempotency.md) — required on the consuming side of an outboxed route.
- [Entity Framework Core Integration](entity-framework-integration.md) — the scoped `DbContext` the
  EF Core store shares with your handler.
- [Capability Matrix](../capability-matrix.md) — the outbox's produce-side row, next to idempotency's.
- `src/Benzene.Outbox/CLAUDE.md`, `src/Benzene.Outbox.DynamoDb/CLAUDE.md`,
  `src/Benzene.Outbox.EntityFramework/CLAUDE.md` — full package internals.
- [`examples/AwsMesh`](../../examples/AwsMesh/README.md#the-outbox-atomic-commit-stream-dispatch-sweep-redrive-dedup-at-the-consumer) —
  the real dogfooded example: `orders-api` commits an order and two outbound sends atomically via
  the DynamoDB unit of work, relayed by streams + a scheduled sweep, deduped by `payments-api`.
