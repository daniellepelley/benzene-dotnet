# Benzene.EventSourcing.DynamoDb

A distributed `IEventStore` (from `Benzene.EventSourcing`) backed by DynamoDB — the production event
log for a fleet, where the in-memory store can't help.

## Shape

- `DynamoDbEventStore : IEventStore`
  - One item per event, keyed `(streamId, version)` — string partition key + numeric sort key.
  - `AppendAsync` is a single `TransactWriteItems`; each event's `Put` is conditional on that
    `(streamId, version)` slot not existing (`attribute_not_exists(#pk)`), so two writers racing the
    same expected version can't both win — **optimistic concurrency without a lock**. On a cancelled
    transaction it reads the current version and throws `EventStoreConcurrencyException`.
  - `ReadAsync` queries the stream partition, sort ascending, paginated.
  - Append is bounded by the DynamoDB transaction limit (100 items); a larger append must be split.
- `AddDynamoDbEventStore(tableName, pk?, sk?)` — registers it as the `IEventStore`, resolving
  `IAmazonDynamoDB` from DI (the consumer registers the client and provisions the table).

## Projections

The table's **DynamoDB stream** is the projection feed: consume it with `Benzene.Aws.Lambda.DynamoDb`
(`[Message("table:INSERT")]`) to build CQRS read models in order, at least once.

## Notes

- The store does not create the table (composite key: string `pk` + numeric `version`).
- Events are immutable — evolve their shape with `AddPayloadVersioning` (upcast on read), never rewrite.
