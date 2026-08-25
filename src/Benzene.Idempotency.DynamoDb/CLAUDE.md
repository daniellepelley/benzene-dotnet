# Benzene.Idempotency.DynamoDb

A distributed `IIdempotencyStore` (from `Benzene.Idempotency`) backed by Amazon DynamoDB — the
production store for cross-instance de-duplication of at-least-once deliveries (SNS/SQS/EventBridge/
Kinesis/DynamoDB-Streams), where the single-process `InMemoryIdempotencyStore` cannot help.

## Shape

- `DynamoDbIdempotencyStore : IIdempotencyStore`
  - `TryClaimAsync` — a conditional `PutItem` (`attribute_not_exists(pk) OR expiresAt < :now`) writes
    an `InProgress` record plus a freshly minted `claimToken` attribute; the condition is what makes
    the first-time claim **atomic** across instances, so concurrent redeliveries can't both win. On
    `ConditionalCheckFailedException` it reads the live record and returns `ClaimResult.AlreadyExists(...)`;
    on a win it returns `ClaimResult.Won(claimToken)`.
  - `CompleteAsync(key, claimToken, wasSuccessful, ct)` — `PutItem` setting `status=Completed` +
    `wasSuccessful`, **conditioned on `claimToken` matching** (see "Claim fencing" below). Returns
    `false` (nothing written) on a condition failure instead of throwing.
  - `ReleaseAsync(key, claimToken, ct)` — `DeleteItem`, likewise conditioned on `claimToken` matching,
    so a failed handler's message is reprocessed on redelivery. Returns `false` on a condition failure.
- `Extensions.AddDynamoDbIdempotencyStore(tableName, timeToLive?, partitionKeyAttribute?)` — registers
  it as the `IIdempotencyStore`, resolving `IAmazonDynamoDB` from DI (the consumer registers the
  client). Pair with `UseIdempotency()` on the pipeline.

## Table

- String partition key (default attribute `pk`).
- Enable **DynamoDB TTL on `expiresAt`** (epoch seconds) so records self-expire after the store's
  time-to-live (default 24h; must exceed the transport's max redelivery window).
- Because DynamoDB TTL deletion lags, the store also treats a record whose `expiresAt` is in the past
  as **absent** on read, so an expired key is reclaimable the instant it lapses.
- `claimToken` — the claim-fencing token, written on every `TryClaimAsync` win and on `CompleteAsync`
  (unchanged); consulted by `CompleteAsync`/`ReleaseAsync`'s `ConditionExpression`. See "Claim fencing".

## Claim fencing
Every winning `TryClaimAsync` mints a fresh opaque token (`ClaimResult.ClaimToken`) and writes it as
the `claimToken` attribute. `CompleteAsync`/`ReleaseAsync` **require** that token back and their
`PutItem`/`DeleteItem` calls set `ConditionExpression = "claimToken = :claimToken"`, so a settle whose
token no longer matches the live claim (it lapsed and was reclaimed by another worker, or was already
settled) hits `ConditionalCheckFailedException`, translated to a `false` return rather than an
exception — nothing is written. This closes the hole where a stale/slow holder's late settle would
otherwise silently clobber whatever the new holder already recorded. See `Benzene.Idempotency/CLAUDE.md`'s
"Claim fencing" section for the full contract.

## Notes

- The store does **not** create the table — that's the consumer's infra.
- `timeToLive` must outlive the transport's redelivery window, or a slow redelivery could be
  reprocessed as new.
