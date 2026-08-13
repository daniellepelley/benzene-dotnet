# Benzene.Outbox.DynamoDb

## What this package does
The AWS-first `IOutboxStore` (from `Benzene.Outbox`) backed by Amazon DynamoDB — the production
store for a multi-instance/Lambda deployment, where `InMemoryOutboxStore` cannot help (state lives
in one process only). It also ships the DynamoDB **unit of work**
(`IDynamoDbOutboxTransaction`) that makes `OutboxWriteMode.Transactional` capture's atomic-commit
promise real: one `TransactWriteItems` call containing the application's own writes plus a `Put` per
staged envelope — see `Benzene.Outbox/CLAUDE.md`'s "Capability boundary" section for why
`Benzene.Outbox` alone cannot deliver that guarantee.

## Table shape
- **Partition key**: a single string attribute holding the envelope's `Id` (default attribute name
  `id`).
- **Sparse GSI** (default name `pending-index`) drives the sweeper
  (`DynamoDbOutboxStore.ClaimDueAsync`): partition key `gsiPk` (always the constant literal
  `"pending"`), sort key `gsiSk` (the envelope's due time — `nextAttemptAtUtc`, or `createdAtUtc` for
  a freshly captured envelope that has never been attempted). Both `gsiPk`/`gsiSk` are written only
  while an envelope is `Pending` and removed by `MarkDispatchedAsync`/`ParkAsync` — the index only
  ever holds envelopes still waiting to go out, so it stays small regardless of dispatch history.
  **The GSI must project `ALL`** — `ClaimDueAsync` reads the full envelope straight off the query
  result, with no follow-up `GetItem` per row.
  - Honest trade-off: a constant-partition GSI serializes sweep throughput onto one logical
    partition. Fine for this feature's throughput class (outbox volume, not primary traffic); the
    DynamoDB-Streams dispatch path (a service's own `Benzene.Aws.Lambda.DynamoDb` handler calling
    `ClaimAsync` directly by id) bypasses this index entirely and doesn't share that bottleneck.
- **TTL**: enable native DynamoDB TTL on the `expiresAt` attribute (epoch seconds). It is set only by
  `MarkDispatchedAsync` (never at capture, never on `Parked`) — so a `Dispatched` envelope self-expires
  after the store's `retentionPeriod` (default 7 days) and a `Parked` envelope is never auto-deleted,
  matching the promise `Benzene.Outbox/CLAUDE.md` makes ("parked is the operator's evidence"). Because
  native TTL owns retention here, `DynamoDbOutboxStore.DeleteDispatchedBeforeAsync` is always a no-op
  returning `0`.
- Other attributes: `topic`, `payload`, `payloadType`, `headers` (a DynamoDB `M` map of string→string,
  possibly empty), `createdAtUtc`, `attemptCount`, `status`, and, when applicable,
  `nextAttemptAtUtc`/`lastError`/`leaseUntil`. All stored timestamps use UTC round-trip (`"O"`/ISO-8601)
  strings so lexical ordering matches chronological ordering (required for `gsiSk`'s sort order).
- **This store never creates the table** (the GSI, TTL config, and base table are the consumer's
  infrastructure) — matches every other DynamoDB-backed Benzene store (`DynamoDbIdempotencyStore`,
  `DynamoDbEventStore`).

## Claim atomicity — the "same discipline as `DynamoDbIdempotencyStore`", adapted to `UpdateItem`
`ClaimDueAsync` (batch, via the sparse GSI) and `ClaimAsync` (single id, the stream-triggered path)
both win a claim with **one conditional `UpdateItem`** setting `leaseUntil`:
```
attribute_exists(#pk) AND #status = :pending
  AND (attribute_not_exists(nextAttemptAtUtc) OR nextAttemptAtUtc <= :now)
  AND (attribute_not_exists(leaseUntil) OR leaseUntil < :now)
```
This is the same hard atomicity requirement `IOutboxStore` and `DynamoDbIdempotencyStore.TryClaimAsync`
both place on their implementations — a stream-triggered dispatcher and a sweeper racing the same
envelope cannot both win it. It reaches the "lapsed lease is reclaimable" honesty
`DynamoDbIdempotencyStore` gets from a *read-back after a failed conditional `PutItem`* differently:
because `UpdateItem`'s condition expression can directly express "free OR lapsed"
(`leaseUntil < :now`), the lapsed case is caught inside the same atomic round trip — no separate
read-back call is needed here (unlike a `PutItem`-based claim, which can only succeed or fail as a
whole and therefore needs the read-back to distinguish "doesn't exist" from "live lease"). A refused
claim (`ConditionalCheckFailedException`) is simply excluded from `ClaimDueAsync`'s result, or
returned as `null` from `ClaimAsync` — no item content leaks either way.

## Key types
- `DynamoDbOutboxStore : IOutboxStore` — see "Table shape" and "Claim atomicity" above.
  `MarkDispatchedAsync`/`RescheduleAsync`/`ParkAsync` are conditional on `attribute_exists`, so a
  call for an envelope that no longer exists is a no-op per the `IOutboxStore` contract (not an
  exception).
- `IDynamoDbOutboxTransaction` / `DynamoDbOutboxTransaction` (scoped) — drains the scope's
  `BufferedOutboxStage`, appends one `Put` `TransactWriteItem` per staged envelope to the caller's
  own application `TransactWriteItem`s, and issues exactly one `TransactWriteItemsAsync`. Throws
  `InvalidOperationException` for a combined item count over DynamoDB's 100-item transaction limit,
  and for a commit with nothing staged and no application items (a caller-side no-op is treated as a
  misconfiguration to surface loudly, not silently swallowed).
- `OutboxStreamImage` — the deserialization target for a DynamoDB Streams `NewImage`, as unmarshalled
  to plain JSON by `Benzene.Aws.Lambda.DynamoDb`'s `DynamoDbMessageBodyGetter` (AttributeValue JSON
  → plain JSON). This package owns the item schema, so the mapping lives here — the eventual Lambda
  relay handler (Phase 3) stays a few lines with no new package dependency of its own. Carries only
  the envelope's business/lifecycle fields (not the store's internal claim-plumbing attributes); has
  `ToEnvelope()` for the rare case a full `OutboxEnvelope` is useful without a round trip back to the
  table. In practice a relay handler only needs `OutboxStreamImage.Id` to call
  `IOutboxDispatcher.DispatchOneAsync(id)`. **No dependency on `Benzene.Aws.Lambda.DynamoDb`** —
  this is a plain POCO deserialized with `System.Text.Json`, matched by convention to what that
  package's body getter hands a handler.
- `DynamoDbOutboxItemMapper` (internal) — the one place the item's attribute layout is built/read,
  shared by `DynamoDbOutboxStore.AddAsync` and `DynamoDbOutboxTransaction.CommitAsync` so both write
  the exact same shape.
- `Extensions` — `AddDynamoDbOutboxStore(tableName, retentionPeriod?, partitionKeyAttribute?,
  pendingIndexName?)` (singleton `IOutboxStore`, resolving `IAmazonDynamoDB` from DI) and
  `AddDynamoDbOutboxTransaction(tableName, partitionKeyAttribute?)` (scoped
  `IDynamoDbOutboxTransaction`, resolving the scope's `BufferedOutboxStage` — requires
  `Benzene.Outbox`'s `AddOutbox(...)` to already be registered).

## Usage
```csharp
// DI
services
    .AddOutbox(o => o.WriteMode = OutboxWriteMode.Transactional)
    .AddDynamoDbOutboxStore("orders-outbox")
    .AddDynamoDbOutboxTransaction("orders-outbox");
// The consumer registers IAmazonDynamoDB itself, e.g.:
services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());

// Outbound routing: capture stages instead of writing immediately.
services.AddOutboundRouting(routing => routing
    .Route("payments:capture", pipeline => pipeline
        .UseW3CTraceContext().UseCorrelationId()
        .UseOutbox()
        .UseSqs(queueUrl)));

// Handler: one atomic TransactWriteItems for the order + the staged envelope(s).
public async Task<IBenzeneResult<Void>> Handle(CreateOrderRequest request, IDynamoDbOutboxTransaction outbox)
{
    await _sender.SendAsync<CapturePaymentRequest, Void>("payments:capture", ToCaptureRequest(request));
    // ^ captured, not sent yet - staged on this scope's BufferedOutboxStage.

    var orderPut = new TransactWriteItem { Put = new Put { TableName = "orders", Item = ToItem(request) } };
    await outbox.CommitAsync([orderPut]);   // ONE TransactWriteItems: order + captured envelope(s).

    return BenzeneResult.Created<Void>();
}
```

## Dependencies on other Benzene packages
- **Benzene.Outbox** — `IOutboxStore`, `OutboxEnvelope`, `OutboxStatus`, `BufferedOutboxStage`.
- **Benzene.Abstractions** — `IBenzeneServiceContainer`/`IServiceResolver`.
- **AWSSDK.DynamoDBv2** (`3.7.301.4`, matching `Benzene.Idempotency.DynamoDb`/
  `Benzene.EventSourcing.DynamoDb`) — the only third-party dependency.
- No dependency on `Benzene.Aws.Lambda.DynamoDb` — `OutboxStreamImage` is a plain POCO matched to
  that package's output shape by convention, not by reference, so this package stays usable outside
  a Lambda deployment (e.g. `Benzene.HostedService`/`Benzene.SelfHost` with `AddDynamoDbOutboxStore`
  and no stream relay at all).

## Conventions
- Time-based logic takes an injectable `Func<DateTimeOffset>` clock (`DynamoDbOutboxStore`),
  matching `DynamoDbIdempotencyStore`/`DynamoDbEventStore`'s constructor pattern.
- Every claim/lifecycle method forwards its `CancellationToken` to the underlying DynamoDB call.
- The store never creates the table, index, or TTL configuration — that is always the consumer's
  infrastructure (Terraform/CloudFormation/CDK), matching the sibling DynamoDB-backed stores.

## Tests
`test/Benzene.Core.Test/Outbox/DynamoDb/` (mocked `IAmazonDynamoDB`, xunit + Moq, no
FluentAssertions — the `DynamoDbIdempotencyStoreTest`/`DynamoDbEventStoreTest` technique):
- `DynamoDbOutboxStoreTest.cs` — `AddAsync` puts one item per envelope; `ClaimDueAsync` queries the
  pending GSI and claims each result with a conditional `UpdateItem`, excluding an item another
  claimer wins the race for; `ClaimAsync` refuses a live lease without reading the item back, and
  returns the envelope on a won claim; `MarkDispatchedAsync` sets `expiresAt` and removes the
  GSI/lease attributes (and is a no-op for a missing envelope); `RescheduleAsync` updates
  `attemptCount`/`nextAttemptAtUtc`/`gsiSk`/`lastError` and releases the lease; `ParkAsync` sets
  `Parked` and removes the GSI/lease attributes; `DeleteDispatchedBeforeAsync` is a no-op that never
  calls DynamoDB.
- `DynamoDbOutboxTransactionTest.cs` — commit combines application items + drained staged envelopes
  into one `TransactWriteItemsAsync` call; the stage is drained (a second commit only sees newly
  staged envelopes); combined count over 100 throws without calling DynamoDB; nothing staged and no
  application items throws; staged-only (no application items) still commits.
- `OutboxStreamImageTest.cs` — deserializes a store-shaped plain-JSON item (as
  `Benzene.Aws.Lambda.DynamoDb` would hand a handler) including the optional `nextAttemptAtUtc`/
  `lastError` fields; `ToEnvelope()` maps every field; an unrecognized `status` string falls back to
  `Pending`.
