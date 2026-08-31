# Round 17 - Reliability Packages Review (2026-08-30)

**Scope, per the brief:** `Benzene.EventSourcing` / `Benzene.EventSourcing.DynamoDb`, `Benzene.Saga`,
`Benzene.Outbox` / `Benzene.Outbox.DynamoDb` / `Benzene.Outbox.EntityFramework`, `Benzene.ClaimCheck` /
`Benzene.ClaimCheck.Aws.S3` / `Benzene.ClaimCheck.Azure.Blob`, `Benzene.Idempotency` /
`Benzene.Idempotency.DynamoDb` - re-reviewed at commit `4389bfb` on `main`. These packages got a
dedicated fix pass in round 11 (task board #121-#182, landed via `4d45ff8` and others) and targeted
fixes since (Saga #15/#208/#209, Idempotency #260/#261) but no dedicated adversarial pass since round
11 - five-plus rounds of the "older, less-recently-reviewed corner" theory this round is testing.

**Method:** read `work/archive/bug-fix-designs-round11-2026-08.md` §2 (Event Sourcing, #121-#132) and
the corresponding `[RESOLVED]`/`Amendment` entries in `work/outstanding-bugs.md` (Event Sourcing WP,
Saga WP-K #208/#209, Outbox/Idempotency WP-3 claim fencing, ClaimCheck #1/#18, round-16 WP-D
idempotency #260/#261) first, so nothing already known/fixed gets re-reported. Then read every
production file in scope end to end, traced the concrete failure scenarios the brief flagged (transact-
write item-limit interaction, compensation ordering, lease/dispatch races, S3/Blob store parity with
the generic ClaimCheck fixes, fencing-token races), and proved or disproved each suspicion with a
throwaway xUnit test added temporarily under `test/Benzene.Core.Test/` and run with
`dotnet test test/Benzene.Core.Test/Benzene.Test.csproj --filter "FullyQualifiedName~<test class>"`
(scoped to the one test project per the resource-sharing note, not the whole solution). Every temporary
test file was deleted immediately after being confirmed red; `git status`/`git diff` confirmed clean
(no source or test files modified) before finishing this review.

Two findings cleared the bar. Both are backed by a red test that fails against the code as it stands at
`4389bfb`; both tests have since been deleted (not committed) per this review's read-only mandate.

---

## Worth-fixing

### 1. `DynamoDbEventStore.AppendAsync`'s own documented 100-event limit produces a transaction that exceeds DynamoDB's real 100-item limit whenever the append targets an existing (non-empty) stream

`src/Benzene.EventSourcing.DynamoDb/DynamoDbEventStore.cs:24,99-204`.

`MaxEventsPerAppend` is `100`, enforced at line 106-111 ("DynamoDB transactions are limited to 100
items. Split the append.") purely by counting `events.Count`. But when `expectedVersion > 0` (i.e. the
append targets a stream that already has at least one event - the overwhelmingly common case for any
long-lived aggregate), `AppendAsync` prepends one extra `TransactWriteItem` - a `ConditionCheck`
verifying the stream is genuinely at `expectedVersion` (the round-11 #121 fix) - *before* the per-event
`Put` items (lines 132-152). The transaction actually sent to AWS therefore has `events.Count + 1`
items, not `events.Count`.

A caller who does exactly what the library's own guard says is fine - append 100 events (`events.Count
== 100 <= MaxEventsPerAppend`) onto a stream that isn't brand new (`expectedVersion > 0`, e.g. `5`) -
gets a `TransactWriteItemsRequest` with **101** items. `TransactWriteItems` has a hard, real AWS limit
of 100 items per call; a request with 101 items is rejected by DynamoDB with a validation error
(`TransactWriteItems.TransactItems` size exceeded) at the API layer, not the friendly
`ArgumentException` the library raises for a 101-event batch. In other words: for any stream that has
already been written to once, the library's own advertised ceiling ("100 events is fine, split anything
larger") is off by one - only 99 events can actually be appended atomically in one call, not 100. The
existing regression test for this limit (`Append_MoreThanTransactionLimit_Throws`) only exercises 101
*events*, and always with the default `expectedVersion: 0` (a fresh stream, where no `ConditionCheck` is
added and 100 events genuinely does produce exactly 100 items) - so the one-more-item-on-a-non-empty-
stream case was never exercised.

**Verified** with a temporary test
(`Append_OfMaxAllowedEventCount_OnAnExistingStream_ExceedsDynamoDbsRealTransactionLimit`): a mocked
`IAmazonDynamoDB`, `store.AppendAsync("acct-1", expectedVersion: 5, <100 events>)` - the library lets
this through with no exception, but the captured `TransactWriteItemsRequest.TransactItems.Count` is
`101`, one over DynamoDB's real per-call limit.

**Suggested fix shape:** either lower the effective per-call cap by one whenever `expectedVersion > 0`
(reject 100 events with a message that explains the reserved condition-check slot), or - preferable,
since it doesn't shrink the usable batch size for the common non-fresh-stream case - drop the extra
`ConditionCheck` item and instead let the per-version `Put`'s own `attribute_not_exists` condition do
double duty by *also* asserting the predecessor version exists via a second condition on the first
`Put` item itself (DynamoDB conditions can reference only the item being written, so this specific
approach needs the fix designer to check feasibility) or simply cap `MaxEventsPerAppend` at 99 and
document why. Any fix should add a test that actually issues a 100-event append at `expectedVersion >
0` and asserts a friendly, pre-flight `ArgumentException` rather than letting AWS's own validation
error surface at request time.

### 2. `DynamoDbIdempotencyStore`'s write-time "is this claim still live" check and its own read-back's "is this claim expired" check disagree at the exact expiry boundary, so a record that the store's own read-back calls expired can spuriously fail to be reclaimed

`src/Benzene.Idempotency.DynamoDb/DynamoDbIdempotencyStore.cs:76-131,199-227`.

`TryClaimAsync`'s conditional `PutItem` (the write that actually wins a claim) treats a record as
"still live, refuse the claim" unless `expiresAt < :now` (line 95, strict less-than - i.e. a record is
only considered expired once `now` has moved *past* `expiresAt`). `ReadRecordAsync` - the read-back this
same method calls immediately after a `ConditionalCheckFailedException`, to decide whether to report
`AlreadyExists` or retry - treats the very same record as expired using `epoch <= now.ToUnixTimeSeconds()`
(line 216, inclusive - i.e. a record is expired *at* `expiresAt`, one second earlier than the write
condition agrees to). The class's own doc comment (lines 16-18) states the store's contract plainly:
"the store also treats a record whose `expiresAt` is in the past **as absent** when it reads one, so an
expired key is reclaimable **the instant it lapses**" - but the write path and the read path don't
actually share that instant.

Concretely: when a record's `expiresAt` equals the current second (`epoch == now`, in the whole-second
granularity both sides use - `ToEpochSeconds`/`ToUnixTimeSeconds` truncate to seconds on both the write
condition's `:now` value and the stored `expiresAt`), the conditional `PutItem` refuses the claim
(condition evaluates false: `expiresAt < now` is false when they're equal), but the read-back that
follows the resulting `ConditionalCheckFailedException` reports the record as expired/absent
(`epoch <= now` is true) and returns `null`. `TryClaimAsync` treats a `null` read-back as "the record
vanished mid-race (e.g. a concurrent `ReleaseAsync`) - retry the same conditional write." With a
genuinely advancing wall clock this self-heals on the very next loop iteration (`now` ticks forward, the
write condition then agrees the record is expired). But the constructor's `now` parameter is explicitly
"Clock, injectable for testing" - and with a fixed/injected clock (the store's own documented,
supported seam), every one of the bounded `MaxClaimAttempts` (3) retries repeats the identical
disagreement, and `TryClaimAsync` throws `IdempotencyClaimContentionException` for a key the store's own
read path considers expired and reclaimable - contradicting the class's documented "reclaimable the
instant it lapses" guarantee for a case that isn't actually contention at all, just this internal
inconsistency.

**Verified** with a temporary test
(`TryClaim_WhenExistingRecordExpiresAtExactlyNow_ContradictsItself_AndThrowsContention_InsteadOfReclaiming`):
a mocked `IAmazonDynamoDB` with a fixed clock and an existing record whose `expiresAt` equals that fixed
`now`; `PutItemAsync` always throws `ConditionalCheckFailedException` (per the strict write condition)
and `GetItemAsync` always returns the record (which `ReadRecordAsync`'s inclusive check treats as
absent/expired). `TryClaimAsync("key-1")` throws `IdempotencyClaimContentionException` after exhausting
3 attempts, rather than returning a won claim.

**Real-world exposure:** with a genuine, continuously-advancing `DateTimeOffset.UtcNow` this is a narrow
window (the reclaiming attempt has to land in the exact same whole second the record's multi-hour TTL
expires) that resolves itself on retry in virtually every case - so it is unlikely to be the dominant
cause of a production contention exception. It is a hard, reproducible, permanent failure only for a
caller using the documented fixed-clock testing seam (or any deployment that feeds this store a
synchronized/quantized logical clock rather than genuine wall-clock time), where every retry sees the
identical `now` and the inconsistency never resolves. Worth fixing because it's a real internal
contradiction between two definitions of "expired" in the same class guarding the same invariant, not
because it is likely to bite most production deployments today.

**Suggested fix shape:** make the two checks agree - either make the write condition inclusive
(`expiresAt <= :now`) to match `ReadRecordAsync`, or make `ReadRecordAsync` strict (`epoch < now`) to
match the write condition. The former (inclusive on both sides) is more consistent with the class's own
stated intent ("reclaimable the instant it lapses").

---

## Reviewed, no finding

- **`InMemoryEventStore`** - no snapshot/replay path exists in this package (`IEventStore`'s own doc
  comment: "rehydration, snapshots, and replay are composed on top" - by design, not a gap); the
  round-11 fixes (#121 stream-at-expected-version check, #125 atomic batch splicing, #128 empty-batch
  concurrency check, #130 cancellation, #131 `MaxEventsPerAppend` parity, #132 no stray empty-stream
  entry) all still hold on read. `IEventStore` has no delete/stream-deletion operation at all, so the
  brief's "concurrent appends racing a stream-deletion" scenario doesn't apply to this store family -
  there is nothing to race against.
- **`DynamoDbEventStore`** otherwise - the #122/#123/#124 conflict-classification/diagnostic-read fixes
  (throttling vs. genuine conflict, inner exception preserved, diagnostic read on its own
  `CancellationToken.None`) all re-verified correct; `ReadAsync`/`CurrentVersionAsync` both correctly use
  `ConsistentRead: true` for the read-your-writes discipline the class remarks describe.
- **`Benzene.Saga`** - compensation ordering under partial failure is correct: `RollBackAsync`
  compensates the failing stage's own concurrently-succeeded steps first, then every earlier completed
  stage newest-first (genuine LIFO across stage boundaries); `#208`'s state-store-failure rollback path
  (`HandleStateStoreFailureAsync`/`RollBackCompletedStagesAsync`) and `#209`'s multi-failure surfacing
  both re-verified correct on read. `SagaContext` is documented and implemented as concurrent-read/
  single-threaded-write with no locking needed, which holds given `Stage.Publish` only runs after each
  stage's `Task.WhenAll` barrier. A saga step's own idempotency under *external* message redelivery
  (as opposed to `SagaRetryPolicy`'s own internal, compensation-gated retry) is explicitly out of this
  package's contract - `Benzene.Saga/CLAUDE.md` documents the engine as in-process/in-memory with "NO
  durable crash-resume" and defers cross-invocation de-duplication to `Benzene.Idempotency` wrapping the
  triggering handler; this is a stated capability boundary, not a silent gap.
- **`Benzene.Outbox` write-mode boundary and lease/dispatch races** - `OutboxMiddleware`'s
  `Immediate`/`Transactional` split, `BufferedOutboxStage`'s drain-once/dispose-warns-if-undrained
  semantics, and `OutboxDispatcher`'s post-send `MarkDispatchedAsync`/`RescheduleAsync`/`ParkAsync`
  fencing (round 5-6 #17, still correct) all read correctly. A dispatch that outruns its own claimed
  lease (slow send, GC pause) can have its lease reclaimed by another worker while still in flight - this
  is explicitly documented on `IOutboxStore.ClaimDueAsync` as the one thing fencing cannot close ("it
  cannot un-send a message a stale claimant already handed to the transport before its lease lapsed");
  it is a stated, deliberate at-least-once boundary, not an undocumented race. `DynamoDbOutboxTransaction`
  (the `Transactional`-mode commit path) correctly validates the staged+application item count against
  the 100-item `TransactWriteItems` limit *before* draining the stage (so a rejected commit doesn't
  destroy retriable staged state) - this is the one place in the reviewed packages that already gets the
  "count every item, not just the caller-visible ones" accounting right that finding #1 above shows
  `DynamoDbEventStore` gets wrong.
- **`Benzene.ClaimCheck.Aws.S3`/`Benzene.ClaimCheck.Azure.Blob`** - both thread the ambient
  `CancellationToken` correctly (S3/Blob SDK calls all take one; no accessor needed here since these
  stores don't independently resolve one - the ambient token comes in as a normal parameter from the
  already-fixed #1 middleware fix) and both correctly refuse a foreign reference
  (`ClaimCheckStoreMismatchException`) rather than fetching across store/bucket/container boundaries.
  Neither backend needs its own "reclaim an expired entry" logic (unlike `InMemoryClaimCheckStore`'s
  #18 fix) because retention for both is delegated to real infrastructure (an S3 lifecycle rule / a Blob
  Storage lifecycle-management policy) that the store's own `GetAsync` naturally cooperates with by
  returning `null` on a 404/NotFound once the object is actually gone - there is no in-process cache to
  go stale. A claim-check payload that fails to upload (the `PutAsync` call itself throwing) correctly
  never reaches `context.Request = new ClaimCheckPlaceholder(...)` - `ClaimCheckOffloadMiddleware`
  throws and the send never proceeds, so there is no reference-without-a-payload case. The inverse (a
  put that succeeds but the *subsequent* send fails, orphaning the now-unreferenced stored payload) is
  explicitly documented in the middleware's own remarks as accepted, TTL-cleaned residue, not a bug.
- **`Benzene.Idempotency`/`Benzene.Idempotency.DynamoDb`** otherwise - `InMemoryIdempotencyStore`'s
  fencing is fully serialized under one lock with a single (not dual, unlike the DynamoDb store) expiry
  definition, so it has no analogous boundary-disagreement window; `IdempotencyMiddleware`'s post-#260
  `WasSuccessful` fix holds on read (a result-bearing context with no result set is correctly treated as
  not-proven-successful). `InProgressBehavior.Skip`'s documented tradeoff (acking/dropping a
  concurrent duplicate whose sibling later fails) is exactly that - a documented, deliberate tradeoff
  with an opt-out (`InProgressBehavior.Throw`) already provided, not a hidden gap.

---

## Overall assessment

This pass found genuinely new material in exactly the two places the brief specifically flagged as
plausible (the DynamoDB event store's own transact-write item accounting, and the DynamoDB idempotency
store's dual expiry definitions) and confirmed everything else the brief flagged - saga compensation
ordering, outbox lease/dispatch races, the S3/Blob claim-check backends, and the idempotency stores'
sequential fencing - is already correct or is an already-documented, deliberate capability boundary.
Consistent with the expectation that findings get rarer each round: five-plus rounds after the last
dedicated pass, these two packages needed a specifically adversarial trace of an item-count arithmetic
detail and a boundary-operator comparison to turn up anything at all; nothing at the level of a race
condition, resource leak, or silent data corruption in the broader sense survived this pass.
