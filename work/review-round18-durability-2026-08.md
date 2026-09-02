# Round 18 - Durability Packages Review (2026-09-01)

**Scope, per the brief:** `Benzene.EventSourcing` / `Benzene.EventSourcing.DynamoDb`, `Benzene.Outbox` /
`Benzene.Outbox.DynamoDb` / `Benzene.Outbox.EntityFramework`, `Benzene.Idempotency` /
`Benzene.Idempotency.DynamoDb`, `Benzene.ClaimCheck` / `Benzene.ClaimCheck.Aws.S3` /
`Benzene.ClaimCheck.Azure.Blob`, `Benzene.Saga`, `Benzene.MapReduce`, `Benzene.ResponseEvents` -
reviewed at commit `7f642b2` on `main`.

**Method:** read `work/review-round17-reliability-2026-08.md` in full (the prior round's dedicated pass
over most of this same territory) and the relevant `[RESOLVED]` entries in `work/outstanding-bugs.md`
first, so nothing already known/fixed gets re-reported. Re-derived round 17's two accepted findings
(`DynamoDbEventStore.AppendAsync`'s 100-item transact-write cap, #271; `DynamoDbIdempotencyStore`'s
write/read expiry-boundary disagreement, #272) by hand against the code as it stands today rather than
trusting the fix commit messages - **both hold**: `DynamoDbEventStore.AppendAsync`
(`src/Benzene.EventSourcing.DynamoDb/DynamoDbEventStore.cs:113-120`) now reserves one item for the
`ConditionCheck` whenever `expectedVersion > 0` and rejects a 100-event append in that case with a
descriptive `ArgumentException`; `DynamoDbIdempotencyStore`'s write condition
(`src/Benzene.Idempotency.DynamoDb/DynamoDbIdempotencyStore.cs:98`) and its read-back's expiry check
(same file, line 219) both now use `<=` against the same whole-second-truncated `now`, so a record
whose `expiresAt` equals the current second is treated as expired by both paths consistently. Then read
every `CLAUDE.md` and every production file in scope end to end, re-traced the concrete failure
scenarios the brief flagged (outbox crash-before-settle, saga compensation ordering/failure-partway,
claim-check offload/hydrate at exactly the size threshold), and cross-checked every boundary comparison
(`<` vs `<=`, `>` vs `>=`) that decides claim/lease/expiry outcomes across all three `IOutboxStore`
implementations and all `IEventStore`/`IIdempotencyStore`/`IClaimCheckStore` implementations against
each other and against their own interface's documented contract - the same technique that found #271/
#272 last round. No dotnet SDK is available in this environment; every conclusion below is from manual
tracing of the exact operators and control flow, not a build/test run. Each finding below states the
regression test that would prove it, for a future fix round with CI access to pick up.

One finding cleared the bar - narrow in real-world exposure (like round 17's #272, its dominant
real-clock case self-heals), but a genuine, reproducible, and currently completely untested
cross-implementation contract violation.

---

## Worth-fixing

### 1. `DynamoDbOutboxStore`'s claim condition uses a strict `leaseUntil < :now`, disagreeing with `InMemoryOutboxStore` and `EntityFrameworkOutboxStore`'s inclusive `LeaseUntil <= now` - and with `IOutboxStore`/`OutboxOptions`'s own documented contract - so a lease that expires at exactly the instant a reclaim is attempted is refused by the DynamoDB store when every other implementation would allow it

`src/Benzene.Outbox.DynamoDb/DynamoDbOutboxStore.cs:263-299` (`TryClaimAsync`, the private helper both
`ClaimDueAsync` and `ClaimAsync` route through).

`IOutboxStore` is one pluggable contract with three shipped implementations
(`InMemoryOutboxStore`, `DynamoDbOutboxStore`, `EntityFrameworkOutboxStore<TDbContext>`), and
`OutboxOptions.ClaimLease`'s own XML doc states the contract in one sentence
(`src/Benzene.Outbox/OutboxOptions.cs:54`): *"How long a claimed envelope's lease is held **before**
another claimer **may** take it over"* - i.e. once the lease's duration has elapsed, a reclaim is
allowed. Two of the three stores implement exactly that, inclusively, at the tick the lease elapses:

- `InMemoryOutboxStore.IsDue` (`src/Benzene.Outbox/InMemoryOutboxStore.cs:208-213`):
  `entry.LeaseUntil == null || entry.LeaseUntil <= now` - due (reclaimable) the instant `now` reaches
  `LeaseUntil`.
- `EntityFrameworkOutboxStore<TDbContext>`'s claim predicate, used identically by both the
  `ExecuteUpdateAsync` fast path and the optimistic-concurrency fallback
  (`src/Benzene.Outbox.EntityFramework/EntityFrameworkOutboxStore.cs:92,131`, and
  `OutboxRecord.IsDue`, `src/Benzene.Outbox.EntityFramework/OutboxRecord.cs:117-118`):
  `r.LeaseUntil == null || r.LeaseUntil <= now` - same inclusive boundary.

`DynamoDbOutboxStore.TryClaimAsync`'s conditional `UpdateItem`
(`src/Benzene.Outbox.DynamoDb/DynamoDbOutboxStore.cs:273-276`) instead uses:

```csharp
ConditionExpression =
    "attribute_exists(#pk) AND #status = :pending " +
    "AND (attribute_not_exists(nextAttemptAtUtc) OR nextAttemptAtUtc <= :now) " +
    "AND (attribute_not_exists(leaseUntil) OR leaseUntil < :now)",
```

The `nextAttemptAtUtc <= :now` conjunct is inclusive, matching the other two stores' due-check exactly.
But the `leaseUntil < :now` conjunct is **strict** - at the exact instant `now == leaseUntil`, this
store's own condition evaluates `leaseUntil < :now` to `false` and refuses the claim, while the other
two stores' `<=` would accept it. This is not a stylistic nit: it is the identical bug *shape* round
17's #272 fixed (`DynamoDbIdempotencyStore`'s write condition disagreeing with its own read-back's
expiry check) - here the disagreement is across sibling implementations of the same interface instead
of within one class, but the mechanism and exposure are the same.

**Concrete failure scenario.** `TryClaimAsync` computes `leaseUntil = now1 + lease` at claim time
(`src/Benzene.Outbox.DynamoDb/DynamoDbOutboxStore.cs:284`, using the constructor's injectable
`Func<DateTimeOffset> now`, explicitly documented as "Clock, injectable for testing"). A later sweep
calls `TryClaimAsync` again with a fresh `now2`. If `now2` happens to equal `leaseUntil` exactly, the
condition's `leaseUntil < :now2` term is `false` and the claim is refused - even though the lease has,
by the class's own doc comment three lines above the condition ("may take the envelope over" once the
lease elapses) and by `OutboxOptions.ClaimLease`'s doc, genuinely already elapsed. With a real,
continuously-advancing `DateTimeOffset.UtcNow` this exact-tick collision is astronomically unlikely (two
independent 100-nanosecond-resolution clock reads landing on the identical tick) and self-heals on the
very next sweep regardless. It becomes a **reliable, reproducible** refusal for exactly the case the
constructor's own clock seam exists to support: a test, or a deployment, that feeds this store a fixed
or quantized clock (e.g. a clock truncated to whole seconds, or a test double that advances time in
discrete jumps landing precisely on `now + lease`) - the same class of exposure round 17's #272 called
out for `DynamoDbIdempotencyStore`'s equivalent boundary bug. Concretely: `store.ClaimAsync("env-1",
lease, ct)` with a clock fixed at `T` claims and sets `leaseUntil = T + lease`; a second `ClaimAsync`
call with the clock advanced to exactly `T + lease` (no further) is refused by `DynamoDbOutboxStore`
but would succeed against `InMemoryOutboxStore`/`EntityFrameworkOutboxStore` given the identical
timeline - a real, observable behavioral difference between implementations of the same contract, not
just an internal inconsistency invisible to callers.

**Why this is currently invisible.** `test/Benzene.Core.Test/Outbox/DynamoDb/DynamoDbOutboxStoreTest.cs`
tests the claim/settle *request shape* against a mocked `IAmazonDynamoDB` (e.g. `Assert.Contains
("leaseUntil", update.UpdateExpression)`) - it never evaluates the condition expression against actual
timestamps, so this exact-boundary case is untested. `InMemoryOutboxStoreTest`'s own lease-lapse tests
advance the clock by a full 2 seconds past expiry (`now.AddSeconds(2)`), not to the exact boundary
either, so no existing test in the suite would catch this even by accident.

**Suggested fix shape:** change `DynamoDbOutboxStore.TryClaimAsync`'s condition to
`leaseUntil <= :now` (inclusive), matching `InMemoryOutboxStore`/`EntityFrameworkOutboxStore` and the
`OutboxOptions.ClaimLease` doc's own "before...may take it over" wording. Add a regression test that
mocks `IAmazonDynamoDB.UpdateItemAsync` to inspect the actual `ExpressionAttributeValues`/comparison
semantics (or, more directly, a small in-memory fake of the conditional-update evaluation) proving a
claim attempt at `now == leaseUntil` succeeds - the DynamoDB-store sibling of
`InMemoryOutboxStoreTest`'s existing lease-lapse coverage, but asserting the exact tick rather than
`now + 2s`.

---

## Reviewed, no finding

- **`Benzene.EventSourcing`/`Benzene.EventSourcing.DynamoDb`** - round 17's #271 fix re-verified
  correct (see above); `InMemoryEventStore`'s parity comment and reduced effective cap for
  `expectedVersion > 0` deliberately mirror `DynamoDbEventStore`'s real constraint even though the
  in-memory store has no physical transaction-size limit of its own, so app code targeting either store
  sees the identical ceiling - correct on read. `ReadAsync`/`CurrentVersionAsync` both use
  `ConsistentRead: true`; the deterministic `ClientRequestToken` (SHA-256 of streamId + expectedVersion
  + event contents, truncated to a GUID) gives idempotent-retry safety with no meaningful collision
  risk. No delete/stream-deletion operation exists on `IEventStore`, so there is nothing for a
  concurrent-append-vs-deletion race to apply to.
- **`Benzene.Idempotency`/`Benzene.Idempotency.DynamoDb`** - round 17's #272 fix re-verified correct
  (see above): the write condition (`expiresAt <= :now`) and the read-back's expiry check
  (`epoch <= now.ToUnixTimeSeconds()`) now agree at the boundary. `InMemoryIdempotencyStore`'s single
  in-lock check has no analogous dual-definition window (confirmed on read: `TryClaimAsync`'s
  `existing.ExpiresAt > now` is the only "is this record live" check anywhere in that class).
  `IdempotencyMiddleware`'s settle-failure-never-masks-the-original-exception path
  (`ReleaseAsync`'s try/catch around `_store.ReleaseAsync`) and its `WasSuccessful` "no result signal
  proves nothing" convention (#229/#260) both hold. `HeaderOrBodyHashIdempotencyKeyStrategy`'s
  length-prefixed hash input correctly prevents the field-boundary collision an earlier round already
  fixed.
- **`Benzene.Outbox` engine (`OutboxMiddleware`/`OutboxDispatcher`/`OutboxDispatcherWorker`/
  `BufferedOutboxStage`)** - the crash-before-settle question the brief specifically asked about is
  handled correctly: `OutboxDispatcher.MarkDispatchedWithRetryAsync` cleanly separates "the send itself
  failed" (reschedule/park path) from "the send genuinely succeeded but the settle call threw"
  (`SentButUnsettled` - retried once, then left claimed for the sweeper to reclaim once the lease
  naturally lapses, never rescheduled/parked as if the send had failed, which would guarantee a
  duplicate). `OutboxDispatcherWorker`'s start/stop/dispose sequencing is race-free (a linked CTS
  disposed only inside the loop's own `finally`, `Dispose()` documented as safe only after `StopAsync`
  returns). `BufferedOutboxStage`'s `Peek()`-then-`DrainStaged()`-only-after-success discipline
  (mirrored by `DynamoDbOutboxTransaction.CommitAsync`, which explicitly builds its transact-item list
  from `Peek()` and only calls `DrainStaged()` after `TransactWriteItemsAsync` has actually returned)
  is correct - a thrown commit leaves staged envelopes in place for a caller's retry rather than losing
  them.
- **`Benzene.Outbox.DynamoDb` otherwise** - `DynamoDbOutboxTransaction`'s non-destructive-peek /
  drain-only-on-success discipline and its pre-drain item-count validation (both already documented as
  fixed, and re-verified correct on read) remain correct; `DynamoDbOutboxItemMapper` writes/reads a
  consistent attribute shape, and its sparse-GSI write (`gsiPk`/`gsiSk` present only while `Pending`,
  removed by `MarkDispatchedAsync`/`ParkAsync`) is symmetric between `AddAsync`'s and
  `DynamoDbOutboxTransaction.CommitAsync`'s use of the same mapper. `DynamoDbOutboxStore.AddAsync`'s
  per-envelope `PutItemAsync` loop (not a single batched/transactional write) is only ever invoked with
  a single-element array today - `OutboxMiddleware.HandleAsync` (`Immediate` mode) is the only caller in
  the codebase, always `_store.AddAsync([envelope])` - so the theoretical "partial failure mid-loop"
  exposure a future multi-envelope caller could hit is not a live bug against any code that exists.
- **`Benzene.Outbox.EntityFramework`** - the `ExecuteUpdateAsync` fast path / optimistic-concurrency
  fallback (for providers like EF Core InMemory that don't support `ExecuteUpdate`) both correctly
  implement the *inclusive* lease boundary (see finding #1 above - this store is one of the two that
  gets it right); the fallback's bounded retry loop correctly detaches a losing entity before retrying
  so a stale tracked instance never blocks the next attempt's read. The three settle methods' shared
  `TrySaveSettleAsync` correctly collapses both the before-read fencing check and the
  between-read-and-`SaveChanges` `RowVersion` optimistic-concurrency race to the same documented `false`
  return, never letting `DbUpdateConcurrencyException` escape.
- **`Benzene.Saga`** - re-verified independently of round 17's already-thorough pass: `Stage.ExecuteAsync`
  awaits every step via `Task.WhenAll` (no fail-fast short-circuit, so every outcome is known before the
  stage is judged); `Saga.RollBackAsync` compensates the failing stage's own steps first, then every
  earlier completed stage strictly newest-first via a plain sequential `for` loop (not concurrent across
  stages, so `SagaContext`'s "writes are single-threaded after each stage's barrier" invariant is never
  violated even during rollback); `SagaStep<T>.CompensateAsync` correctly no-ops a step whose forward
  never succeeded, treats a succeeded step with no compensation delegate as "nothing to undo", and
  catches a throwing compensation into `CompensationFailed` rather than aborting the rest of the
  rollback (best-effort, as documented). `SagaRetryPolicy` correctly retries only a clean `RolledBack`
  outcome, never `PartiallyRolledBack`. `Saga.RunOnceAsync`'s state-store-failure handling (#208/#257)
  re-verified correct: every store call is wrapped in `RecordSafelyAsync`, a store throw never skips
  compensation for effects already applied and never replaces a genuinely successful/rolled-back
  `SagaResult` with a raw exception.
- **`Benzene.ClaimCheck`/`Benzene.ClaimCheck.Aws.S3`/`Benzene.ClaimCheck.Azure.Blob`** - the
  offload/hydrate round trip at exactly the size threshold is correct:
  `ClaimCheckOffloadMiddleware.HandleAsync`'s `byteCount < _options.ThresholdBytes` guard means a
  message whose serialized size equals the threshold offloads (matches the package's own documented "at
  or over a configurable threshold" behavior; no off-by-one). `InMemoryClaimCheckStore`'s `GetAsync`
  (`entry.ExpiresAt > _now()`) and its sweep (`pair.Value.ExpiresAt <= now`) agree with each other at
  the boundary (an entry expiring at exactly `now` is correctly treated as expired by both paths) -
  unlike the DynamoDB outbox store's disagreement above, this one class's two expiry checks are
  consistent. Both cloud stores (`S3ClaimCheckStore`, `BlobClaimCheckStore`) correctly refuse a foreign
  reference before ever calling the backing SDK, and both correctly map a 404/`NotFound` to `null`
  rather than throwing.
- **`Benzene.MapReduce`** - `ScatterGatherAsync`'s per-shard try/catch correctly excludes
  `OperationCanceledException` from being folded into a "failed shard" outcome (letting a genuine
  cancellation propagate as cancellation, matching `RetryMiddleware`'s own `DefaultShouldRetry`
  convention); `ThrowOnAnyFailure` vs `BestEffort` both correctly reduce only over the outcomes that
  actually succeeded; an empty `shards` collection reduces cleanly to `seed` with no sender call at all.
- **`Benzene.ResponseEvents`** - `ResponseEventsMiddleware`'s `PublishFailureMode.FailMessage` path
  correctly stops publishing further matches and replaces the response with `UnexpectedError` (documented
  fan-out-stops-on-first-failure behavior, not a bug); `LogAndContinue` correctly keeps going through
  every mapping regardless of an earlier one's failure. `CrudConventionResponseEventMapping`'s
  `$"{sourceTopic.Id}d"` past-tense construction is correct for all three convention verbs (`create`/
  `update`/`delete` each become `created`/`updated`/`deleted` by literal `+ "d"` suffix - deliberate, not
  a coincidence the code relies on unknowingly). `ResponseEventMappings.Resolve`'s multiple-matches-fan-
  out behavior is correct and already documented (see #94 in `work/outstanding-bugs.md`).

---

## Overall assessment

This territory continues to earn the "genuinely fertile" framing only in a narrowing sense: round 17
already re-derived and fixed the two real defects a full adversarial pass could find after five-plus
quiet rounds (#271, #272), and this round's independent re-derivation confirms both fixes are correct
as they stand today. Applying the exact same lens one level further - not just within one class's two
definitions of "expired", but across all three implementations of `IOutboxStore` and their shared
interface's documented contract - turned up one more instance of the identical bug *shape*: a
strict-vs-inclusive boundary disagreement, in `DynamoDbOutboxStore`'s lease-reclaim condition, that
`work/outstanding-bugs.md` has no record of and that the existing test suite (request-shape assertions
against a mock, and lease-lapse tests that skip past the boundary by two full seconds) does not exercise
at all. Its real-world production exposure is as narrow as #272's was - a real, continuously-advancing
wall clock essentially never lands two independent reads on the identical tick - but it is a genuine,
currently-undetected contract violation reachable through the store's own documented fixed-clock testing
seam, and worth closing for the same reason #272 was: internal consistency of a documented guarantee
matters even when the production blast radius is small. Everything else the brief specifically flagged -
outbox crash-before-settle handling, saga compensation ordering and best-effort partial-failure recovery,
and claim-check behavior at the exact size threshold - was re-traced by hand and found correct, matching
round 17's conclusion that the broad race-condition/resource-leak/silent-corruption categories in this
territory are exhausted; what remains findable here is boundary-condition arithmetic, one comparison
operator at a time.
