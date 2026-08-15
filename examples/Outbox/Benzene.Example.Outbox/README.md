# Benzene transactional outbox example

A runnable, no-infrastructure walkthrough of [`docs/patterns/transactional-outbox.md`](https://github.com/daniellepelley/benzene/blob/main/docs/patterns/transactional-outbox.md)
and the real, shipped `Benzene.Outbox` package (see [`docs/cookbooks/transactional-outbox.md`](../../../docs/cookbooks/transactional-outbox.md)
and `src/Benzene.Outbox/CLAUDE.md`): the **dual-write problem** — a handler that commits a write and
then separately sends an event has a crash window where the write is real but the send never happens
— and what each of `Benzene.Outbox`'s two write modes actually guarantees about closing it.

This uses the real package end to end (`UseOutbox()`, `AddInMemoryOutboxStore()`, `IOutboxDispatcher`),
not a hand-rolled stand-in. For the real atomic-commit story on real infrastructure (DynamoDB's
`TransactWriteItems`), see [`examples/AwsMesh`](../../AwsMesh/README.md#the-outbox-atomic-commit-stream-dispatch-sweep-redrive-dedup-at-the-consumer) —
this example is the place to start first, since it needs no AWS account to run.

## The scenarios (see `Program.cs`)

1. **The problem** (`NaiveDualWrite`) — no Benzene involved on purpose. A plain write, then a
   separately-failable send, with a simulated crash between them. The order is committed; nothing
   downstream ever hears about it.
2. **`OutboxWriteMode.Immediate`** (the default — durable capture, not atomic with the state write):
   - The handler writes the order, then calls `IBenzeneMessageSender.SendAsync` on a route with
     `UseOutbox()`. Capture writes the envelope straight to the store and returns immediately.
   - Simulated crash/restart: `IOutboxDispatcher.RunOnceAsync()` finds the pending envelope and sends
     it for real, through the same outbound route pipeline.
   - A second pass is correctly a no-op.
   - **At-least-once, not exactly-once**: this example manually claims a fresh envelope and sends it
     — mirroring exactly what `OutboxDispatcher.DispatchEnvelopeAsync` does internally — but
     deliberately skips the final `MarkDispatchedAsync`, simulating a crash right there. The next pass
     redelivers the same event. A real consumer must be idempotent (see
     [`docs/cookbooks/idempotency.md`](../../../docs/cookbooks/idempotency.md)) for that to be safe —
     `Benzene.Outbox` stamps an `idempotency-key` header by default so `Benzene.Idempotency`'s
     `UseIdempotency()` dedups this for free.
   - **What this mode does *not* solve**: the order write and the outbox capture are still two
     separate operations. A crash *before* `SendAsync` is even called is the same hole as scenario 1.
3. **`OutboxWriteMode.Transactional`** (the write and the capture commit together):
   - `UseOutbox()` now stages the envelope into `IOutboxStage` instead of writing it.
     `TransactionalOrders` (`Store/TransactionalOrders.cs`) is a minimal unit of work that drains the
     stage and persists it alongside the order — the same *shape* `Benzene.Outbox.DynamoDb`'s
     `IDynamoDbOutboxTransaction`/`Benzene.Outbox.EntityFramework`'s same-`DbContext` flow use for
     real. **This example's version is illustrative, not atomic** — see the caveat in
     `TransactionalOrders`'s own doc comment; a real deployment needs one of those two store packages.
   - A rejected order never calls the unit of work's commit: the scope disposes with the staged
     envelope undrained, discarded — no order row, no phantom envelope.

## Run it

```bash
dotnet run --project examples/Outbox/Benzene.Example.Outbox
```

## What this example doesn't cover

- **Real atomicity.** `Benzene.Outbox` alone (with `InMemoryOutboxStore`) cannot make
  `Transactional` mode's atomic-commit promise real — that needs `Benzene.Outbox.DynamoDb` or
  `Benzene.Outbox.EntityFramework`, backed by an actual database transaction. See `examples/AwsMesh`
  for the real thing.
- **Stream-triggered dispatch.** This example only calls `IOutboxDispatcher.RunOnceAsync()` (the
  poll-loop sweep). AWS Lambda's near-real-time path (`DispatchOneAsync` off a DynamoDB Streams
  `INSERT`) is also demonstrated in `examples/AwsMesh`.
- A real relay is `OutboxDispatcherWorker`, a poll loop wired into a host; here `RunOnceAsync()` is
  called explicitly at each step so the narrative is visible in the console output.
