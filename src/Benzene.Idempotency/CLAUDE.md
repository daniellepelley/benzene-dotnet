# Benzene.Idempotency

## What this package does
Message de-duplication for at-least-once transports. On SQS, Service Bus, Event Hub, Kafka (and any
other transport that can redeliver a message), the same message can be delivered more than once. This
package adds a pipeline middleware that ensures the handler's side effect runs **at most once** per
idempotency key: the first delivery is processed and its key recorded; redeliveries of that key
short-circuit without re-invoking the handler.

Persistence is pluggable via `IIdempotencyStore` so the dedupe record can live wherever suits the
deployment — no database opinion is baked in. **This package ships `InMemoryIdempotencyStore`
(single-process, for one host / tests).** For a fleet, use a distributed store: the sibling
`Benzene.Idempotency.DynamoDb` package provides `DynamoDbIdempotencyStore` (atomic conditional-write
claim + TTL) via `AddDynamoDbIdempotencyStore(...)`; any store backed by an atomic
conditional write (Redis `SET NX`, a unique-key insert) works the same way.
See the [Capability Matrix](../../docs/capability-matrix.md) for why cross-instance de-duplication
can't be solved by the in-memory store alone.

## Capability boundary — cross-instance dedup is NOT solved in-box
This package gives you the pipeline seam and a single-process in-memory store. It does **not**
provide cross-instance exactly-once. Benzene instances are independent processes (e.g. separate
Lambda invocations) that don't know about one another, so two concurrent redeliveries can land on
two instances at the same moment. `InMemoryIdempotencyStore` cannot see across processes, and simply
bolting on a shared store does not fully close the gap — it can just relocate the race (an instance
can crash after claiming but before completing, a store write can lag, etc.). See
`work/1.0-release-plan.md` §2 principle 6 and the §6 capability-honesty matrix.

The honest solution lives partly outside Benzene:
- **Supply your own `IIdempotencyStore` backed by an external store with atomic conditional-write
  semantics** — DynamoDB conditional `PutItem` (attribute_not_exists), Redis `SET key val NX`, or a
  unique-key insert. The whole guarantee rests on that write being atomic.
- **Design handlers to be naturally idempotent** where possible (upserts keyed on a business id,
  conditional writes), so a slipped-through duplicate is harmless rather than catastrophic.

This middleware is a best-effort de-duplication optimisation on top of those, not a distributed
exactly-once guarantee on its own.

## Key types
- `IdempotencyMiddleware<TContext>` — the middleware. Derives a key, atomically claims it, invokes the
  rest of the pipeline only on the first sighting, and records/releases the outcome afterwards.
- `IIdempotencyStore` — the pluggable persistence contract with **atomic** claim semantics
  (`TryClaimAsync` / `CompleteAsync` / `ReleaseAsync`). Implementations MUST make claim+insert atomic
  (Redis `SET NX`, a unique-key insert) so concurrent redeliveries can't both win the claim.
  **Fenced** — see "Claim fencing" below.
- `InMemoryIdempotencyStore` — default in-process store (dictionary + lock + TTL). Single-instance only.
- `IIdempotencyKeyStrategy<TContext>` — derives the key from a message. Swap it to key on a business
  identifier instead of the default.
- `HeaderOrBodyHashIdempotencyKeyStrategy<TContext>` — default strategy: prefers a caller-supplied
  `idempotency-key` header, else a deterministic SHA-256 hash of topic + body.
- `IdempotencyOptions` — header name, whether to hash the body when no header is present, a key
  prefix, and `InProgressBehavior`.
- `ClaimResult` / `IdempotencyRecord` / `IdempotencyStatus` — the store's data model.
- `IdempotencyConflictException` — thrown for an in-progress duplicate when
  `InProgressBehavior.Throw` is configured.
- `Extensions` — `UseIdempotency<TContext>(configure?)` (pipeline) and
  `AddInMemoryIdempotencyStore(ttl?)` (DI).

## How the outcome is decided
After `next()` runs, the middleware records completion only on success, so a transient failure never
permanently suppresses a message:
- handler **throws** → claim released → redelivery reprocesses (exception rethrown).
- context is `IHasMessageResult` and the result is **unsuccessful** → claim released → redelivery
  reprocesses.
- otherwise (no throw, and either no result signal or a successful one) → recorded **completed**.

On a duplicate of a completed key, the middleware sets a **synthetic** successful `IBenzeneResult`
(`BenzeneResult.Ok()`, when the context is `IHasMessageResult`) so the transport acknowledges/completes the duplicate rather
than looping. Note this is a fresh success signal — the original first-attempt response/payload is
**not** stored or replayed; a duplicate HTTP-style caller does not get the original body back.

## Claim fencing (`ClaimToken`)
Every winning `TryClaimAsync` mints a fresh opaque `ClaimResult.ClaimToken`, non-null exactly when
`Claimed` is `true`. `CompleteAsync`/`ReleaseAsync` **require** that token back
(`CompleteAsync(key, claimToken, wasSuccessful, ct)` / `ReleaseAsync(key, claimToken, ct)` — no default
parameter, no overload; pre-1.0, a skippable fence is no fence) and return `Task<bool>`: `false` means
there was no live claim with that token — it lapsed and was reclaimed by another worker, or was
already settled — and **nothing was written**. `IdempotencyMiddleware` logs a **warning** (not an
error) on a `false` settle: the new holder owns the outcome, so this is expected under contention, not
a failure of the current attempt.

This closes a real hole: without fencing, a stale/slow holder's late `Complete`/`Release` (arriving
after its claim naturally lapsed and a different worker won a fresh claim on the same key) would
silently clobber whatever the new holder had already recorded. `InMemoryIdempotencyStore` checks the
token under the same lock the claim itself uses; `DynamoDbIdempotencyStore` stores the token as a
`claimToken` attribute and conditions the settle `PutItem`/`DeleteItem` on it matching. A custom store
MUST implement the same fencing — see `IIdempotencyStore`'s XML docs for the exact contract each method
must honor.

## `InProgressBehavior`
A duplicate that arrives while the first copy is still `InProgress`:
- `Skip` (default) — drop it without invoking the handler; never double-processes, but a duplicate is
  lost if its sibling later fails before releasing.
- `Throw` — throw `IdempotencyConflictException` so the transport redelivers later (by which time the
  sibling has usually finished).

## Usage
```csharp
// DI: register a store once.
services.AddInMemoryIdempotencyStore(TimeSpan.FromHours(24));   // or your own IIdempotencyStore

// Pipeline: add the middleware before UseMessageHandlers, after logging/tracing.
app.UseIdempotency<MyContext>(o => o.HeaderName = "idempotency-key");
```
`UseIdempotency` uses a registered `IIdempotencyKeyStrategy<TContext>` if present, otherwise the
default header/body-hash strategy (resolving the transport's `IMessageHeadersGetter`,
`IMessageBodyGetter`, `IMessageTopicGetter`, which every `UseMessageHandlers` transport registers).

## Dependencies on other Benzene packages
- **Benzene.Abstractions.Middleware** — `IMiddleware`/pipeline builder.
- **Benzene.Core.MessageHandlers** — transport-agnostic message accessors (`IMessageHeadersGetter`,
  `IMessageBodyGetter`, `IMessageTopicGetter`), `IHasMessageResult` carrying an `IBenzeneResult`.

## Conventions
- Engine is transport-agnostic; persistence is pluggable (mirrors how `Benzene.Saga` keeps state
  storage out of the engine). Do not bake in a specific database.
- A custom store's `TryClaimAsync` MUST be atomic — the whole guarantee rests on it. A custom store's
  `CompleteAsync`/`ReleaseAsync` MUST be fenced on the claim token (see "Claim fencing" above) — the
  same discipline `IOutboxStore`'s lease fencing follows.
- `TryClaimAsync`/`CompleteAsync`/`ReleaseAsync` all take a `CancellationToken`. `InMemoryIdempotencyStore`
  has no downstream I/O to cancel, but still honors it (`ThrowIfCancellationRequested()` before taking
  the lock) rather than silently ignoring an already-cancelled caller. A custom store backed by real
  network I/O (Redis, DynamoDB) MUST forward the token to the underlying client call — see the Redis
  example in `docs/cookbooks/idempotency.md` — so a caller-side cancellation actually aborts the round
  trip instead of the token being accepted-but-ignored.
- `IdempotencyMiddleware<TContext>.HandleAsync` itself has no `CancellationToken` to forward: it
  implements `IMiddleware<TContext>.HandleAsync(TContext, Func<Task>)`, and that pipeline-wide
  interface carries no cancellation token (a framework-wide characteristic, not specific to this
  package — see `Benzene.Abstractions.Middleware/CLAUDE.md`). The middleware therefore always calls
  the store with the default token; a store that needs cancellation (e.g. to bound a slow network
  call) should apply its own timeout/cancellation internally rather than expect one from the caller.

## Tests
- `test/Benzene.Core.Test/Idempotency/InMemoryIdempotencyStoreTest.cs` — store semantics (claim wins
  once, refused while in-progress, completed outcome recorded, release/expiry allow re-claim, keys
  independent), plus claim-fencing: matching-token settle succeeds, stale-token settle is refused and
  writes nothing, and `StaleHolder_LateCompleteAndRelease_AfterLegitimateReclaim_AreRejected_NotClobbered`
  reproduces the round-5 scenario (a stale holder's claim lapses, a second worker reclaims, the stale
  holder's late `Complete`/`Release` are both rejected without touching the new holder's state).
- `test/Benzene.Core.Test/Idempotency/DynamoDb/DynamoDbIdempotencyStoreTest.cs` — same fencing contract
  against the DynamoDB store's `ConditionExpression`-based settle writes.
- `test/Benzene.Core.Test/Idempotency/IdempotencyMiddlewareTest.cs` — first-time processes+records;
  duplicate short-circuits; completed-duplicate replays a successful result; throw/failed-result
  release the claim; no-key passes through; in-progress duplicate skip vs. throw.
- `test/Benzene.Core.Test/Idempotency/HeaderOrBodyHashIdempotencyKeyStrategyTest.cs` — header key
  preferred, prefix applied, deterministic body hash, disabling body-hash returns null.
