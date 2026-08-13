# Benzene.Outbox

## What this package does
The transactional outbox for the produce side, closing the gap consume-side dedup
(`Benzene.Idempotency*`) and the event log (`Benzene.EventSourcing*`) don't: today a handler that
must "write state AND send a message" does two independent operations, and a crash or transport
failure between them silently loses one side (see `examples/AwsMesh/Orders/Handlers/OrderHandlers.cs`'s
try/catch-and-log around its downstream sends for a live example of the hole).

This package makes the outbox an **outbound-route middleware**, not a new send API:
`UseOutbox()` is added to a route's pipeline (`OutboundRoutingBuilder.Route(...)`), placed after
cross-cutting stamping middleware and before the terminal transport converter. It is opt-in per
route - a service outboxes exactly the routes that add it; every other route behaves exactly as
before. `IBenzeneMessageSender.SendAsync` stays the one handler-facing send API; nothing about the
call site changes.

This package (`Benzene.Outbox`) ships the complete host-agnostic engine, usable end to end in a
single process: capture middleware, the envelope/store/stage abstractions, the dispatch engine, a
poll-loop worker, and `InMemoryOutboxStore`. It does **not** ship a store that can make the
"transactional" write mode's atomic-commit promise real - see "Capability boundary" below and the
sibling packages `Benzene.Outbox.DynamoDb` / `Benzene.Outbox.EntityFramework`.

## Capability boundary - be honest about what each write mode guarantees
Neither write mode is exactly-once. Read this before picking one.

- **`OutboxWriteMode.Immediate` (the default): store-and-forward.** Capture writes the envelope
  straight to `IOutboxStore.AddAsync`. This guarantees the send survives process death and transport
  outages, is retried with backoff, and is never silently swallowed - it replaces the try/catch
  pattern outright. It does **not** make the send atomic with the handler's own state write: the
  envelope write and the state write are still two independent operations, just both now durable
  (rather than one durable and one best-effort).
- **`OutboxWriteMode.Transactional`: the actual atomic story - but this package alone can't deliver
  it.** Capture stages the envelope into the scoped `IOutboxStage` (`BufferedOutboxStage` by
  default) instead of writing it. Nothing in `Benzene.Outbox` ever drains that buffer into storage -
  that's the job of an **outbox-aware unit of work** shipped by a store package:
  - `Benzene.Outbox.DynamoDb` (Phase 2) - the handler commits through
    `IDynamoDbOutboxTransaction.CommitAsync(applicationItems)`, issuing ONE `TransactWriteItems`
    containing the app's items plus a `Put` per staged envelope. Bounded by DynamoDB's 100-item
    transaction limit, shared with the app's own items.
  - `Benzene.Outbox.EntityFramework` (Phase 4) - the outbox entity lives in the application's own
    `DbContext`; the stage adds rows to that `DbContext`'s change tracker (never calling
    `SaveChanges` itself), and the handler's own `SaveChangesAsync` commits state + envelopes in one
    database transaction.
  - If you set `WriteMode = Transactional` but never install one of those (or your own unit of work
    that drains `IOutboxStage`), staged envelopes are silently discarded when the scope disposes
    (with a warning log from `BufferedOutboxStage.Dispose` if any were never drained) - **the write
    never happens**. This is consistent by construction (no state was written either, since nothing
    ever committed), but it means `Transactional` mode without a store package installed is a
    misconfiguration, not a smaller guarantee.
- **Delivery is always at-least-once**, end to end, regardless of write mode - duplicates are
  possible (a crash after a successful send but before `MarkDispatchedAsync`; a stream-triggered
  dispatch racing a sweep). `Benzene.Outbox` and `Benzene.Idempotency` are designed as a pair for
  exactly this reason (see "Pairs with Benzene.Idempotency" below); there is no exactly-once claim
  anywhere in this package.
- **No ordering guarantee** across envelopes. A sweep orders by `CreatedAtUtc` best-effort only, a
  stream-triggered relay has no such ordering at all, and retries reorder regardless. Per-key
  ordered dispatch is not implemented.
- **Poison envelopes are parked, not dead-lettered.** After `OutboxOptions.MaxAttempts` (default 10,
  exponential backoff base 30s capped at 1h), an envelope becomes `OutboxStatus.Parked` and is never
  auto-retried or auto-deleted - it is the operator's evidence. This package has no dead-letter
  forwarding story; parking is the deliberate boundary.
- **`InMemoryOutboxStore` is single-process.** Envelopes captured on one instance are only ever
  dispatched by that same instance's `OutboxDispatcherWorker`/`IOutboxDispatcher`. Use a shared store
  (`Benzene.Outbox.DynamoDb`) for a multi-instance deployment.

## Key types
- `OutboxMiddleware : IMiddleware<OutboundContext>` / `UseOutbox(configure?)` - the capture point.
  Terminal on a fresh send (builds an `OutboxEnvelope`, writes/stages it per `WriteMode`, sets
  `context.Response = BenzeneResult.Accepted<Void>()`, never calls `next`); pass-through on a relay
  dispatch (re-applies the envelope's stored headers - stored values win over the relay host's own
  ambient stamping - and calls `next()` so the send reaches the real transport).
- **Constraint:** because capture always sets an `IBenzeneResult<Void>` response, an outboxed route
  is fire-and-forget only. `SendAsync<TRequest, TResponse>` with any `TResponse` other than `Void`
  gets the existing `OutboundResponseTypeMismatchException` from `DefaultBenzeneMessageSender` - the
  same behavior a send-acknowledgement-only transport (SQS/SNS/...) already produces. Request/response
  topics cannot be outboxed; a deferred send has no response to give.
- `OutboxEnvelope` - the immutable captured-send record: id (also the default idempotency key),
  topic, serialized payload + its assembly-qualified type, the post-stamping header snapshot,
  attempt/retry bookkeeping, and lifecycle status.
- `OutboxStatus` - `Pending` / `Dispatched` / `Parked`.
- `OutboxOptions` - **one class serving two independent configuration surfaces** (see its own
  xmldoc remarks): `UseOutbox(configure)` builds its own instance and only reads `WriteMode`/
  `StampIdempotencyKey` from it; `AddOutbox(configure)` builds a *separate* instance, registered
  singleton, that the dispatch engine reads for everything else (`MaxAttempts`, `BackoffBase`,
  `BackoffCap`, `RetentionPeriod`, `BatchSize`, `ClaimLease`, `PollInterval`). A route that wants
  `Transactional` mode must say so at its own `UseOutbox(...)` call even if `AddOutbox(...)` set
  something else - they don't share state.
- `IOutboxStore` - the pluggable persistence contract: `AddAsync`, `ClaimDueAsync`/`ClaimAsync`
  (**must be atomic/conditional per envelope** - the same hard requirement
  `IIdempotencyStore.TryClaimAsync` places on its implementations), `MarkDispatchedAsync`,
  `RescheduleAsync`, `ParkAsync`, `DeleteDispatchedBeforeAsync` (a native-TTL store may no-op this).
- `InMemoryOutboxStore` - the in-process default (dictionary + lock + lease), registered via
  `AddInMemoryOutboxStore()`.
- `IOutboxStage` / `BufferedOutboxStage` - the scoped staging seam for `Transactional` mode (see
  "Capability boundary" above). `DrainStaged()` returns and clears the buffer; disposing with
  undrained envelopes logs a warning.
- `OutboxDispatchScope` - the scoped marker+headers holder `IOutboxDispatcher` sets before re-sending
  an envelope, and `OutboxMiddleware` reads to decide capture vs. pass-through. Deliberately **not**
  carried on `OutboundContext` itself - see `Benzene.Abstractions.Middleware/CLAUDE.md`'s "Context
  purity" section; this is the same pattern `Benzene.Core.MessageHandlers`' `PresetTopicHolder` uses.
- `IOutboxDispatcher` / `OutboxDispatcher` - the host-agnostic dispatch engine. `RunOnceAsync` claims
  a due batch and dispatches each (returning dispatched/rescheduled/parked/deleted counts), then
  deletes retention-expired dispatched envelopes. `DispatchOneAsync(id)` claims-and-dispatches one
  specific envelope - the stream-triggered relay path (Phase 2/3's DynamoDB Streams handler calls
  straight into this). Dispatching an envelope creates a fresh DI scope
  (`IServiceResolverFactory.CreateScope()`), marks that scope's `OutboxDispatchScope`, deserializes
  the payload, and re-sends through that scope's `IBenzeneMessageSender` - the exact same route
  pipeline (transport, retry, health checks) an inline send would have used.
- `OutboxDispatcherWorker : IBenzeneWorker` - a poll loop calling `RunOnceAsync` on
  `OutboxOptions.PollInterval` (default 5s), with a graceful stop that finishes an in-flight run.
  This package does not depend on `Benzene.HostedService` or `Benzene.SelfHost` - wire the resolved
  worker into whichever host you use (see "Usage" below). **Not suitable for AWS Lambda** - see
  "Relay hosts" below.
- `OutboxDefaults.IdempotencyKeyHeaderName` - `"idempotency-key"`, a **deliberate value duplicate**
  of `Benzene.Idempotency.IdempotencyDefaults.HeaderName`. `Benzene.Outbox` does not take a package
  reference on `Benzene.Idempotency` for one string, so it stays installable (and testable) without
  the idempotency package present. If you change one constant's value, change the other.
- `Extensions` - `UseOutbox(configure?)` (pipeline), `AddOutbox(configure?)` / `AddInMemoryOutboxStore(now?)` /
  `AddOutboxDispatcherWorker()` (DI).

## Pairs with `Benzene.Idempotency`
At-least-once delivery means consumers need dedup. At capture, if the route's headers lack
`idempotency-key` (`OutboxDefaults.IdempotencyKeyHeaderName`), `OutboxMiddleware` stamps the
envelope's own id into that header (`OutboxOptions.StampIdempotencyKey`, default `true`). A consumer
running `Benzene.Idempotency`'s `UseIdempotency()` with the default
`HeaderOrBodyHashIdempotencyKeyStrategy` then dedups relay redeliveries with zero extra
configuration - the two packages click together by default without either referencing the other.

## Relay hosts
- **`Benzene.HostedService` / `Benzene.SelfHost`:** resolve `OutboxDispatcherWorker` (or
  `IBenzeneWorker`) and wire it into the host yourself, e.g. with `Benzene.SelfHost`:
  ```csharp
  app.Workers.Add(resolverFactory =>
  {
      using var scope = resolverFactory.CreateScope();
      return scope.GetService<IBenzeneWorker>();
  });
  ```
- **AWS Lambda:** there is no background thread, so `OutboxDispatcherWorker`'s poll loop does not
  apply. The documented pattern (built out in Phase 2/3 via `Benzene.Outbox.DynamoDb`) is DynamoDB
  Streams dispatch (near-real-time; the service's own Lambda consumes `INSERT` events and calls
  `IOutboxDispatcher.DispatchOneAsync`) **plus** a low-frequency scheduled sweep (`RunOnceAsync`,
  which also handles retries/parking/cleanup that a single stream `INSERT` can't). Streams alone are
  not sufficient - `INSERT` fires once, so retries need the sweeper as a backstop.
- **Other FaaS (Azure Functions, ...):** same shape (a change-feed/timer trigger calling into
  `IOutboxDispatcher`), deferred alongside a future Cosmos store.

## Usage
```csharp
// DI: register the engine, a store, and (optionally) the poll-loop worker.
services
    .AddOutbox(o => o.MaxAttempts = 10)      // dispatch engine's shared options
    .AddInMemoryOutboxStore()                // or a real store, e.g. Benzene.Outbox.DynamoDb
    .AddOutboxDispatcherWorker();            // optional - only if this process also relays

// Outbound routing: opt one route into the outbox. Order matters - after stamping, before transport.
services.AddOutboundRouting(routing => routing
    .Route("payments:capture", pipeline => pipeline
        .UseW3CTraceContext()
        .UseCorrelationId()
        .UseOutbox(o => o.WriteMode = OutboxWriteMode.Immediate)
        .UseSqs(queueUrl)));

// Handler code is unchanged - it still just calls SendAsync<TRequest, Void>.
await sender.SendAsync<CapturePaymentRequest, Void>("payments:capture", request);
```

## Dependencies on other Benzene packages
- **Benzene.Clients** - `OutboundContext`, `IBenzeneMessageSender`, `OutboundRoutingBuilder`'s
  pipeline builder, `OutboundResponseTypeMismatchException`.
- **Benzene.Abstractions** (+ **.Middleware**, **.Pipelines**) - `IMiddleware`/pipeline builder,
  `IBenzeneServiceContainer`/`IServiceResolver`/`IServiceResolverFactory`, `ISerializer`,
  `IBenzeneWorker` (home: `Benzene.Abstractions.Pipelines`, namespace `Benzene.Abstractions.Hosting`).
- **Benzene.Results** - `BenzeneResult`/`IBenzeneResult`/`Void`.
- No third-party dependencies. No dependency on `Benzene.Idempotency` (see the `OutboxDefaults`
  mirror note above) or on any hosting package (`Benzene.SelfHost`/`Benzene.HostedService`).

## Conventions
- Engine is transport-agnostic and store-pluggable, matching `Benzene.Idempotency`'s shape.
- `IOutboxStore`'s claim methods MUST be atomic - the dispatch engine's at-least-once (not
  at-least-twice-by-default) behavior rests entirely on that, exactly like `IIdempotencyStore`.
- All `IOutboxStore`/`IOutboxStage` methods take a `CancellationToken` and forward it to any
  downstream I/O.
- Time-based logic takes an injectable `Func<DateTimeOffset>` clock (`InMemoryOutboxStore`,
  `OutboxDispatcher`), matching `DynamoDbIdempotencyStore`'s constructor pattern.
- Registration extends `IBenzeneServiceContainer` and uses `TryAdd*` throughout, so a caller can
  override any piece (a custom `IOutboxStore`, a custom `IOutboxStage`) by registering it first.

## Tests
- `test/Benzene.Core.Test/Outbox/OutboxMiddlewareTest.cs` - capture short-circuits with `Accepted`;
  non-`Void` `TResponse` throws the mismatch exception; dispatch marker passes through and stored
  headers win over ambient stamps; idempotency-key stamped only when absent; store failure
  propagates.
- `test/Benzene.Core.Test/Outbox/InMemoryOutboxStoreTest.cs` - claim/lease/due semantics, reschedule,
  park, retention cleanup, independence across ids.
- `test/Benzene.Core.Test/Outbox/BufferedOutboxStageTest.cs` - staging buffers, drain returns and
  clears, undrained-on-dispose warns.
- `test/Benzene.Core.Test/Outbox/OutboxDispatcherTest.cs` - run-once success/reschedule/park paths,
  retention cleanup, `DispatchOneAsync`'s claim-refused path.
- `test/Benzene.Core.Test/Outbox/OutboxDispatcherWorkerTest.cs` - starts/stops cleanly, polls on the
  configured interval, survives a failing run.
