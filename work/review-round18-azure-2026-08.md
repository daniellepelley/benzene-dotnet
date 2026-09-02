# Round 18 - Azure transports and adapters (2026-09-01)

**Scope, per the brief:** the full Azure Functions trigger family (`Benzene.Azure.Function.Core`/
`.AspNet`/`.SourceGenerators`/`.Timer`/`.BlobStorage`/`.CosmosDb`/`.EventGrid`/`.EventHub`/`.Kafka`/
`.QueueStorage`/`.ServiceBus`), the three self-hosted workers (`Benzene.Azure.CosmosDb`,
`Benzene.Azure.EventHub`, `Benzene.Azure.ServiceBus`), the four outbound clients
(`Benzene.Clients.Azure.EventGrid`/`.EventHub`/`.QueueStorage`/`.ServiceBus`), the storage-backed
stores (`Benzene.ClaimCheck.Azure.Blob`, `Benzene.Mesh.Azure.Blob`, `Benzene.Mesh.Discovery.Azure`),
`Benzene.HealthChecks.Azure.ServiceBus`, and the two relational packages
(`Benzene.Outbox.EntityFramework`, `Benzene.HealthChecks.EntityFramework`) — read against `7f642b2`
on `main`. Cross-checked against `work/outstanding-bugs.md`'s full resolved history (rounds 5-17) and
against `work/review-round17-azure-deep-2026-08.md` in full before starting, per the brief, so nothing
that round already found/ruled on is re-reported here.

**Method:** read every file in scope end to end (including every package's own `CLAUDE.md` for its
documented intent before judging its code against it), traced concrete failure scenarios by hand for
anything that looked suspicious, and cross-referenced each one against the shared infrastructure it
composes with (`AzureFunctionBatchApplicationBase`, `BoundedFanOut`, `HealthCheckError.Classify`) to
confirm whether a shared fix already covers it. **No dotnet SDK is available in this environment** —
nothing below was built or executed; every conclusion (bug or clean) comes from manual tracing against
the actual shipped code and, where relevant, the actual EF Core / Azure SDK semantics the code depends
on. No test files were created or modified.

**Result: no findings clear the bar this round.** This is the second Azure-focused pass in three
rounds (16 = full sweep, 17 = targeted deep-dive, 18 = this one), and the territory shows it — every
scenario that looked promising on first read turned out to already be covered by a shared fix (#257/
#258's infra-escalation and ambient-cancellation guards, #276/#277's Cosmos/Service-Bus settlement
isolation, WP-K's `HealthCheckError.Classify` cancellation rethrow) once traced to where it actually
resolves. Below is what was specifically checked and why each one doesn't clear the bar, since a
"nothing found" round is only useful if it shows real scrutiny rather than a skim.

---

## Areas checked with no new finding

### 1. `Benzene.Outbox.EntityFramework` — full read, no bug found

This package (never reviewed in an Azure-territory pass before — it's SQL/relational, not
Azure-specific, but sits in scope this round) is unusually carefully engineered and its own `CLAUDE.md`
already documents the two subtle correctness properties that would otherwise be the first places to
look for a bug:

- **`RowVersion` is a real EF concurrency token.** `ModelBuilderExtensions.AddOutboxEntities` calls
  `entity.Property(r => r.RowVersion).IsConcurrencyToken()` — verified directly, since a hand-rolled
  "concurrency token" that was never actually registered with EF as one would make
  `TrySaveSettleAsync`'s whole `DbUpdateConcurrencyException`-catching design silently inert (every
  `SaveChangesAsync` would just overwrite unconditionally). It's wired correctly.
- **Claim fencing race window.** `MarkDispatchedAsync`/`RescheduleAsync`/`ParkAsync` all read-then-write
  keyed on `leaseToken`, and the between-read-and-save race is closed by `TrySaveSettleAsync` catching
  `DbUpdateConcurrencyException` and returning `false` — traced through `OutboxRecord.Touch()` being
  called before every settling `SaveChanges`, confirming the concurrency check actually fires.
- **`ClaimAsync`'s `ExecuteUpdateAsync`-then-fallback-to-optimistic-concurrency shape** (catching
  `InvalidOperationException` to detect a provider that doesn't support `ExecuteUpdate`, e.g. EF Core
  InMemory) is a single atomic DB statement on the fast path, so there's no read-then-write race window
  to worry about there at all.

One thing considered and deliberately not reported: `ClaimDueAsync` claims each candidate id in a
`foreach` loop via `ClaimAsync`, and a cancellation mid-loop discards the `claimed` list built so far —
those rows are left leased-but-never-returned-to-the-caller until their lease naturally expires. This
is real, but it's the same "give up mid-sweep, recover via lease expiry" shape every lease-based
at-least-once claim loop in this codebase has (nothing Benzene does duplicates or loses the underlying
row), and isn't EF-specific or Azure-specific — it doesn't clear the bar as a concrete, this-package
bug.

### 2. `Benzene.HealthChecks.EntityFramework` — full read, no bug found

Both checks (`DatabaseConnectionHealthCheck<TDbContext>`, `DatabaseHealthCheck<TDbContext>`)
deliberately catch `OperationCanceledException` first and rethrow, with a comment explaining exactly
why (`ExceptionHandlingHealthCheck` needs to see it to classify it as "Cancelled", not "the database is
broken"). This is the correct pattern — see item 4 below for why the three Azure client health checks
that *don't* have this explicit guard are nonetheless correct too, via a different, centralized
mechanism.

### 3. `Benzene.ClaimCheck.Azure.Blob` / `Benzene.HealthChecks.Azure.ServiceBus` — full read, no bug found

Both packages are small, single-purpose, and match their `CLAUDE.md`s exactly. Specifically checked:
`BlobClaimCheckStore.ValidateAndExtractKey`'s mismatch guard (a foreign container/scheme/prefix throws
rather than silently fetching cross-store — traced that a `PutAsync`-issued key can never be crafted to
escape its own prefix check, since the prefix is prepended before the topic, not appended after);
`ServiceBusHealthCheck`'s receiver lifecycle (`CreateReceiver` → `PeekMessageAsync` → `finally
DisposeAsync`, correctly ordered so a peek exception still disposes the receiver).

### 4. Azure client health checks (`ServiceBusHealthCheck`, `EventHubHealthCheck`,
`QueueStorageHealthCheck`) — traced a specific near-miss to ground, confirmed correct

All three (`Benzene.HealthChecks.Azure.ServiceBus`, `Benzene.Clients.Azure.EventHub`,
`Benzene.Clients.Azure.QueueStorage`) catch a bare `catch (Exception ex)` around their SDK probe call
with **no explicit `OperationCanceledException` guard**, unlike `DatabaseHealthCheck`/
`DatabaseConnectionHealthCheck` above. On first read this looked exactly like the class of bug
`HealthChecks.EntityFramework`'s own comments warn against: a caller-driven cancellation (the
processor's own `TimeOutHealthCheck` wrapper firing, or ambient shutdown) getting misclassified as an
ordinary transient dependency failure instead of the distinct "Cancelled" outcome.

Traced it through and it's **not** a bug: all three route their caught exception through
`HealthCheckError.Classify(type, ex, ...)` (`Benzene.HealthChecks.Core`), and `Classify` itself opens
with:

```csharp
if (exception is OperationCanceledException)
{
    throw exception;
}
```

— documented explicitly as WP-K's fix (`Benzene.HealthChecks.Core/CLAUDE.md`): *"Re-thrown, never
classified... nearly every caller of this method reaches it from a blanket `catch (Exception ex)` that
cannot itself distinguish the two... Re-throwing here... lets `ExceptionHandlingHealthCheck`... turn it
into the distinct `Cancelled` outcome... Fixed once here so every caller gets it for free."* So the
`catch` block in all three checks still runs, but `Classify` immediately rethrows the same
`OperationCanceledException` instance instead of returning a result — it propagates back out through
the check's own `catch`, past any `finally` (confirmed `ServiceBusHealthCheck`'s receiver-disposal
`finally` still runs correctly on the way out), and up to `ExceptionHandlingHealthCheck`, which is
exactly where the EF checks' own hand-rolled guard was trying to land it. This is precisely the
"fixed once here so every caller gets it for free" design working as intended — a genuinely different
(and, once traced, correct) idiom from the EF checks' explicit guard, not a gap. No bug; recorded here
because it's the kind of thing worth confirming rather than assuming.

### 5. `AzureFunctionBatchApplicationBase` and every transport built on it (ServiceBus, EventGrid,
EventHub, Kafka, QueueStorage) — re-verified #257/#258/#276/#277 are intact and unbypassed

Read `ProcessItemAsync` in full against the round-16/17 rulings: the infrastructure-escalation carve-out
(`if (isInfrastructure) throw;`), the token-verified ambient-cancellation carve-out, and the
hook-can-never-mask-the-original-failure guard (`OnExceptionCaughtAsync`/`CleanUpBeforeRethrowAsync`
each in their own try/catch-and-log) are all present exactly as documented, and every one of the five
transport packages built on this base plugs in only its own `CreateProcessingException`/`GetLogId`/
`FailureLogMessageTemplate`/`GetLogger` — no transport reintroduces a bypassing catch block. Also
re-verified `BenzeneServiceBusWorker.HandleMessageAsync` and `BenzeneCosmosChangeFeedWorker
.OnChangesAsync` directly against the round-17 `#276`/`#277` fixes (settlement moved outside the
handler's own try/catch, skip-mode checkpoint now guarded) — both fixes are present in the current
source, unmodified since round 17.

### 6. `Benzene.Azure.Function.SourceGenerators` — checked specifically for the unescaped-interpolation
bug class (per the brief), no bug found

Every string value read off a `[Benzene*Trigger]` attribute (Name, QueueName, TopicName,
SubscriptionName, EventHubName, ConsumerGroup, BrokerList, Topic, Path, DatabaseName, ContainerName,
Connection, LeaseContainerName, Route, Schedule) is interpolated into the generated C# only after
passing through `AttributeReading.Literal(value)`, which calls Roslyn's own
`SymbolDisplay.FormatLiteral(value, quote: true)` — the correct, robust API for producing a safe,
quoted C# string literal (handles embedded quotes, backslashes, and control characters), unlike the
hand-rolled `EscapeCSharpString`-style helpers other codegen packages in this codebase needed because
they don't already depend on `Microsoft.CodeAnalysis.CSharp`. This package does, so it uses the SDK's
own escaping instead of a parallel hand-rolled one. Also checked `NamedType` (used for Cosmos DB's
`DocumentType`, interpolated as a generic-type-argument string) — this comes from
`ITypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)`, a Roslyn-generated identifier
string, not raw user text, so it's safe by construction rather than needing escaping. Every one of the
nine transport readers in `Transports/MessagingTransports.cs` plus `Transports/Http.cs` was checked
individually against this pattern — no interpolation site skips `Literal()`.

### 7. `BoundedFanOut` callers in this territory — no caller ignores cancellation for a still-queued
item

Per the brief's callout (round 15 found one caller of `BoundedFanOut` ignoring cancellation for queued
items), checked every caller in scope: `AzureFunctionBatchApplicationBase.HandleBatchAsync` (the shared
base for ServiceBus/EventGrid/EventHub/Kafka/QueueStorage) passes `cancellationToken` straight through
to `BoundedFanOut.WhenAllAsync` as its own parameter — this is the fixed, round-15-hardened call site,
unchanged. No other `BoundedFanOut` call site exists in this territory (Blob Storage and Cosmos DB have
no batch fan-out to bound at all — single-item and stream-fan-in respectively, as their own `CLAUDE.md`s
document).

### 8. Disposal of Azure SDK clients across this territory — no `IAsyncDisposable`-only-without-a-bridge
gap found

Checked every `Dispose()`/`DisposeAsync()` in scope against the round-16/17 `#266`/`#262` bug class
(a container-owned service implementing only `IAsyncDisposable`, torn down through a synchronous
`Dispose()` path that throws). Unlike the Redis/Microsoft-DI cases those rounds found, **every SDK
client in this territory is caller-owned, not container-owned by any Benzene type**:

- The four outbound clients (`EventHubBenzeneMessageClient`, `ServiceBusBenzeneMessageClient`,
  `QueueStorageBenzeneMessageClient`, `EventGridBenzeneMessageClient`) each have a `Dispose()` that is a
  **documented no-op** ("the caller owns the `X`'s lifetime") — there's nothing for these wrapper types
  to dispose, so there's no synchronous-bridge-to-async-disposal hazard to have.
  `BenzeneServiceBusWorker`/`BenzeneEventHubWorker` (the self-hosted workers) only ever dispose the
  processor(s) they themselves created (`await _processor.DisposeAsync()` — genuinely async, no sync
  bridge needed since `StopAsync` is already async), never the client the caller's factory returned —
  documented and tested (`StopAsync_DisposesTheProcessor_ButNeverTheClientItDidNotExclusivelyOwn`).
- `EventProcessorClient` (the self-hosted Event Hub worker's processor) has no `Dispose`/`DisposeAsync`
  at all in the SDK — teardown is `StopProcessingAsync` only, so there's no disposal contract to get
  wrong.

### 9. Settlement/failure-handling carve-outs specific to Kafka and Event Hub (`EscalateUnestablishedOutcome
= false`) — re-verified as the documented, deliberate carve-out, not a bug

Both `KafkaBatchApplication` and the Event Hub fan-out path override the base class's default (escalate
an unset `MessageResult` like a failure) back to "ack on null", with the base class's own carve-out
comment and each package's `CLAUDE.md` explaining why: neither transport has a per-record dead-letter
path, so escalating an unrouted record would replay the whole triggered batch forever. This is one of
the deliberately-mixed "flip rows and carve-out rows" `work/settlement-consistency-fix-plan.md` already
calls out by name as something not to "fix" blanket-style — traced both overrides against that plan and
confirmed neither has drifted from what it documents.

### 10. `Benzene.Mesh.Azure.Blob` / `Benzene.Mesh.Discovery.Azure` — spot-checked, no new finding beyond
round 17's dedicated pass

Round 17 already gave these two packages a dedicated deep-dive against the `#150`/`#151` atomicity/
isolation bar. Re-read `BlobMeshArtifactStore` directly this round (not just the round-17 summary) —
`PublishAsync`'s single atomic `UploadAsync` call and `TryReadAsync`'s 404-to-null mapping are
unchanged and match what round 17 verified. `IMeshArtifactStore`'s interface contract has no
`CancellationToken` parameter at all (true for every implementation — S3, filesystem, Blob alike), so
this store forwarding no token is a shared interface-level design choice, not a per-implementation
gap specific to Azure.

---

## Overall assessment

This territory has now had a full sweep (round 16), a targeted deep-dive on two specific under-examined
corners (round 17), and this round's full-territory re-read with a source-generator-escaping and
`BoundedFanOut`-caller focus per the brief. All three passes converge on the same conclusion: the
Azure Functions trigger family's shared infrastructure
(`AzureFunctionBatchApplicationBase`/`HealthCheckError.Classify`) genuinely does what its extensive
doc comments claim, every transport built on it inherits the fix rather than bypassing it, and the two
relational/storage packages added to this round's scope (`Benzene.Outbox.EntityFramework`,
`Benzene.HealthChecks.EntityFramework`) are new-ish but already carefully built to the same standard.
Nothing in this pass rises to a genuine, concrete, reproducible bug — every promising lead resolved to
either an already-fixed defect (verified still fixed) or a correct-but-non-obvious design (verified
correct by tracing the actual call path, not by assuming). No regression tests are proposed this round
since there is nothing to regress-test.
