# Benzene CQRS / read model example

A runnable, no-infrastructure walkthrough of [`docs/patterns/cqrs-read-models.md`](https://github.com/daniellepelley/benzene/blob/main/docs/patterns/cqrs-read-models.md)'s
worked example: **"a tenant and all its users"** — a cross-aggregate query that no single
share-nothing [core service](https://github.com/daniellepelley/benzene/blob/main/docs/patterns/core-services.md)
is allowed to answer (the tenant service may never know its users exist), served instead by a
**read model**: a separate, derived, denormalized view projected from the domain events the write
side emits.

Composes with the other two patterns the doc describes as a trilogy:

- **[The transactional outbox](../../Outbox/Benzene.Example.Outbox)** — this example reuses it
  directly (`UseOutbox()` + `AddInMemoryOutboxStore()`) so the read model never misses an event.
- **[Choreography](https://github.com/daniellepelley/benzene/blob/main/docs/patterns/choreography.md)** —
  the write side publishes `tenant:created`/`user:created` with no idea a read model is listening.

## The scenarios (see `Program.cs`)

1. **The write side** — `Tenants` and `Users` (`WriteSide/`) are two separate, share-nothing stores.
   Each write captures its event via the outbox instead of sending it inline.
2. **The read model lags** — querying `GetTenantWithUsersHandler` immediately after the writes finds
   nothing yet; the events haven't been relayed. The core services themselves are still fully current
   if you read them directly instead — the deliberate per-query choice the pattern doc calls out.
3. **Relay, then one indexed read** — `IOutboxDispatcher.RunOnceAsync()` delivers both events into the
   read side's own message-handler pipeline (`ReadSide/ProjectionHandlers.cs`), which folds them into
   `ReadStore`. The query now answers "tenant + all its users" in one read, no fan-out.
4. **Idempotent replay** — the same `user:created` event is folded into the read store a second time
   (a stand-in for an at-least-once redelivery, or a full rebuild). The user count doesn't change —
   `ReadStore.AddUserToTenant`'s upsert converges instead of duplicating.
5. **Out-of-order arrival** — a `user:created` for a brand-new tenant is projected before that
   tenant's own `tenant:created` has arrived (`Benzene.Outbox` makes no ordering guarantee across
   envelopes). The read model stands up a placeholder shell rather than erroring, and resolves
   correctly once the tenant event does arrive — order-independent by construction.

## Run it

```bash
dotnet run --project examples/Cqrs/Benzene.Example.Cqrs
```

## What this example doesn't cover

- **A real relay host.** Like `examples/Outbox`, this only calls `IOutboxDispatcher.RunOnceAsync()`
  explicitly, rather than running `OutboxDispatcherWorker`'s poll loop or a stream-triggered dispatch.
- **A separate read-side process.** The write side and the read side share one `ServiceCollection`
  and one process here, purely because this is a single-file demo — nothing about the design assumes
  that. A real read model is its own deployable service, consuming events over whatever transport
  [choreography](https://github.com/daniellepelley/benzene/blob/main/docs/patterns/choreography.md)
  uses (SQS, Service Bus, Pub/Sub, ...), with its own database.
- **A real projection database.** `ReadStore` is an in-memory dictionary; a real read model's store is
  whatever fits the query shape (a relational table, a document store, a search index).
