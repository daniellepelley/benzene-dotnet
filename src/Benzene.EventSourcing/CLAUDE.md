# Benzene.EventSourcing

A deliberately tiny, unopinionated event-sourcing core: an append-only, ordered event log with
optimistic concurrency. Benzene ships no heavy aggregate framework — this is the load-bearing seam
(the store) you compose the rest on.

## Shape

- `IEventStore` — `AppendAsync(streamId, expectedVersion, events)` (optimistic concurrency; throws
  `EventStoreConcurrencyException` if the stream moved, or `ArgumentOutOfRangeException` for a
  negative `expectedVersion` — both `InMemoryEventStore` and the sibling DynamoDB store throw the same
  exception type for the same caller mistake, so app/test code sees identical behavior against either)
  and `ReadAsync(streamId, fromVersion=0)` (ordered events). One stream per aggregate; a stream's
  version is its event count.
- `EventEnvelope` (append) / `StoredEvent` (read) — serialization-agnostic: the caller serializes a
  domain event into `Payload` + an `EventType` discriminator, and deserializes on read. No JSON
  opinion baked in.
- `InMemoryEventStore` — single-process (tests / one host); `AddInMemoryEventStore()`.

## Composing the pattern (app-level, by design)

- **Command handler** rehydrates (fold `ReadAsync` events into state), decides, `AppendAsync`es the
  new event(s) with the version it read (optimistic concurrency).
- **Projections** consume the log's change stream — on AWS, DynamoDB Streams via
  `Benzene.Aws.Lambda.DynamoDb` (`[Message("table:INSERT")]`) into CQRS read models.
- **Rehydration / snapshots / replay** are pure folds you write; the store stays minimal.
- **Event evolution** — upcast historical events on read with `AddPayloadVersioning` (events are
  immutable; never rewrite them).

For a fleet, use a distributed store (e.g. the sibling DynamoDB event store), not the in-memory one.
