# Benzene.Idempotency.DynamoDb

A distributed `IIdempotencyStore` (from `Benzene.Idempotency`) backed by Amazon DynamoDB — the
production store for cross-instance de-duplication of at-least-once deliveries (SNS/SQS/EventBridge/
Kinesis/DynamoDB-Streams), where the single-process `InMemoryIdempotencyStore` cannot help.

## Shape

- `DynamoDbIdempotencyStore : IIdempotencyStore`
  - `TryClaimAsync` — a conditional `PutItem` (`attribute_not_exists(pk) OR expiresAt < :now`) writes
    an `InProgress` record; the condition is what makes the first-time claim **atomic** across
    instances, so concurrent redeliveries can't both win. On `ConditionalCheckFailedException` it
    reads the live record and returns `ClaimResult.AlreadyExists(...)`.
  - `CompleteAsync` — unconditional `PutItem` setting `status=Completed` + `wasSuccessful`.
  - `ReleaseAsync` — `DeleteItem`, so a failed handler's message is reprocessed on redelivery.
- `Extensions.AddDynamoDbIdempotencyStore(tableName, timeToLive?, partitionKeyAttribute?)` — registers
  it as the `IIdempotencyStore`, resolving `IAmazonDynamoDB` from DI (the consumer registers the
  client). Pair with `UseIdempotency()` on the pipeline.

## Table

- String partition key (default attribute `pk`).
- Enable **DynamoDB TTL on `expiresAt`** (epoch seconds) so records self-expire after the store's
  time-to-live (default 24h; must exceed the transport's max redelivery window).
- Because DynamoDB TTL deletion lags, the store also treats a record whose `expiresAt` is in the past
  as **absent** on read, so an expired key is reclaimable the instant it lapses.

## Notes

- The store does **not** create the table — that's the consumer's infra.
- `timeToLive` must outlive the transport's redelivery window, or a slow redelivery could be
  reprocessed as new.
