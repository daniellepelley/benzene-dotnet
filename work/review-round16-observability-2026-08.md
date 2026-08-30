# Round 16 - Observability Review (2026-08-30)

**Scope, per the brief:** the mesh tracing/observability packages -
`Benzene.Mesh.Fleet.Jaeger`, `Benzene.Mesh.Fleet.Tempo`, `Benzene.Mesh.Fleet.Aws.XRay`,
`Benzene.Mesh.Tracing.Tempo`, `Benzene.Mesh.Collector`, `Benzene.Mesh.Dispatch`, `Benzene.Mesh.Aggregator`,
plus general logging/health-check observability - re-reviewed at commit `28473b0` on `main`, with
particular attention to the round-15 merge conflict hand-resolved in
`JaegerTraceSource.SearchAcrossServicesAsync` (cancellation-token fix WP-C vs. per-item try/catch
isolation fix WP-I).

**Method:** read every file in scope end to end against the rounds 9-15 fix record
(`work/outstanding-bugs.md`, `work/archive/bug-fix-designs-round1{0..5}-2026-08.md`) so nothing already
known/accepted gets re-reported, then built concrete failure scenarios for anything that looked
suspicious and proved or disproved them with throwaway xUnit tests run against the real assemblies
(`dotnet test test/Benzene.Mesh.Test/Benzene.Mesh.Test.csproj --filter ...`), added temporarily under
`test/Benzene.Mesh.Test/`, never committed, deleted/reverted immediately after each was confirmed
red. Five findings cleared the bar - all backed by a red test that fails against the code as it
stands at `28473b0`. `git status`/`git diff` confirmed clean (no source or test files modified) before
finishing.

---

## Worth-fixing

### 1. `JaegerTraceSource`/`TempoTraceSource`'s per-service fetch isolation doesn't survive an HttpClient-level timeout that isn't the caller's own token

`src/Benzene.Mesh.Fleet.Jaeger/JaegerTraceSource.cs:130` and `src/Benzene.Mesh.Fleet.Tempo/TempoTraceSource.cs:90`
both isolate a per-service/per-trace fetch with:

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
```

The intent (per #189, and the class's own doc comment) is: one service's connection failure must not
discard every other service's already-fetched results via `Task.WhenAll`'s fault semantics, while a
*genuine* host cancellation should still propagate. The filter distinguishes those two cases purely
by exception *type* - but `OperationCanceledException` (and its subclass `TaskCanceledException`) is
exactly what an `HttpClient`-level per-request `Timeout` throws when *that one request* hangs,
completely independent of whether the caller's own `cancellationToken` was ever cancelled. This
codebase already knows the distinction matters - `Benzene.Core.MessageHandlers/MessageHandler.cs:71,104`
checks `ex.CancellationToken.IsCancellationRequested`, not just the exception's type, for exactly this
reason - but the two trace-source fan-outs never learned it.

Concretely: if one Jaeger/Tempo backend among several configured services is slow enough to trip
`HttpClient.Timeout` (or any `DelegatingHandler`/proxy layer that raises a `TaskCanceledException` not
tied to the caller's token), that per-service exception is *not* caught by the isolation `catch` -
it propagates straight through `BoundedFanOut.WhenAllAsync`'s `Task.WhenAll`, faulting the entire
fan-out and discarding every other service's already-successful results. This is precisely the
regression class #189 fixed, reintroduced for one exception family. The caller-facing effect: a
`mesh:query:correlation`/recent-flows lookup that should degrade to "the healthy services' results,
minus the one slow backend" instead throws entirely and returns nothing, the moment any one backend
in a multi-service mesh is merely slow rather than actually down.

**Verified** with a temporary test added to `JaegerTraceSourceTest.cs`
(`GetCorrelationAsync_IsolatesAPerServiceTimeout_NotTiedToTheCallersToken`): two configured services,
`orders-api` returns a normal trace, `billing-api`'s handler throws
`new TaskCanceledException("simulated HttpClient.Timeout for this one service, unrelated to the
caller's token")` - the caller's own `cancellationToken` is `CancellationToken.None` throughout, so
nothing the caller did requested cancellation. Expected: `orders-api`'s result survives (matching the
already-passing `HttpRequestException` isolation test right next to it). Actual: the
`TaskCanceledException` propagates out of `GetCorrelationAsync` uncaught, and the test fails with that
exception rather than returning a `CorrelationView`. The identical mechanism applies to
`TempoTraceSource.GetCorrelationAsync`'s per-trace fetch isolation (same catch-filter, same
`BoundedFanOut.WhenAllAsync` fault semantics) - not independently reproduced here since it's the same
code shape, but worth fixing in both places together.

The fix shape the codebase already uses elsewhere: filter on
`ex is OperationCanceledException oce && oce.CancellationToken == cancellationToken` (or at minimum
`cancellationToken.IsCancellationRequested`) rather than on exception type alone, so only a
cancellation actually traceable to the caller's own token is left to propagate.

### 2. `MeshCollectorStore.AddEvents` throws `NullReferenceException` on a `null` element inside a non-null events list, dropping the rest of the batch

`src/Benzene.Mesh.Collector/MeshCollectorStore.cs:154-215`. #234 already fixed the case of the whole
`events` list itself deserializing to `null` (`"events": null`). This is the one level down that #234
didn't cover: `MeshTraceEvent` is a reference type, so a wire payload can perfectly legally
deserialize `"events": [null, {...}, {...}]` into a non-null `List<MeshTraceEvent>` that itself
contains a `null` *element* (exactly how a hand-rolled or buggy producer, or a `.NET`/Go client
serializing a `nil`-in-slice, would look on the wire). `AddEvents`'s loop dereferences
`traceEvent.SpanId`/`traceEvent.Service` unconditionally (line 179) with no null-guard on the loop
variable itself, so the first `null` element throws `NullReferenceException` - which is not caught
anywhere between here and the message handler, so:

- every event *before* the null one in the batch has already mutated `_services`/`_topics`/`_ring`
  (the loop processes events one at a time, in order, with no transactional rollback);
- every event *after* the null one is silently dropped, never ingested;
- the caller never receives the `Ack{Accepted=N}` the handler is supposed to return - the exception
  propagates out of `TracesMessageHandler.HandleAsync`, and is caught only by the generic
  "any other exception becomes a service-unavailable result" fallback in `MessageHandler<TRequest,TResponse>`.

This directly contradicts the file's own stated invariant (repeated at the null-`events`-list guard
just above, at `Register`'s null-list guards, and at `AddIssues`'s null-list guard): "no missing feed
ever fails ingestion" (spec §6). A single malformed element partially corrupts a batch's ingestion
and turns an otherwise-successful `mesh:traces` call into a hard failure for the sender.

**Verified** with a temporary test added to `MeshCollectorStoreTest.cs`
(`AddEvents_NullElementInEventsList_DoesNotThrow_AndAppliesTheOtherEvents`): a batch of
`[before, null, after]` should ingest both non-null events (`accepted == 3`,
`topic.Invocations == 2`, matching the null-`Status`-field tolerance test right above it in the same
file). Actual: `NullReferenceException` at `MeshCollectorStore.cs:179`, thrown from inside `AddEvents`
before `after` is ever processed.

### 3. `MeshDispatchRateLimiter.Prune()` can delete a just-created current-minute window, silently resetting a caller's count mid-window

`src/Benzene.Mesh.Dispatch/MeshDispatchRateLimiter.cs:83-94`. `Prune()` enumerates `_windows` and, for
each entry whose captured `Window.Start` is older than the current minute, calls the **unconditional**
two-argument `_windows.TryRemove(pair.Key, out _)` - remove-by-key, with no check that the dictionary
still holds the *same value* it decided was stale. `MeshDispatchGuardMiddleware.HandleAsync` calls
`_limiter.Prune()` immediately before every single `TryAcquire` call
(`src/Benzene.Mesh.Artifacts/MeshDispatchGuardMiddleware.cs:180-181`), i.e. on every guarded dispatch
request - so this isn't a background-timer edge case, it runs under exactly the concurrent load the
limiter exists to bound.

The race: at a minute boundary, one thread's `Prune()` enumerator reads a stale (previous-minute)
`Window` for some key. Before that thread reaches its `TryRemove` call, a second concurrent request
for the *same* key (e.g. the same identity or target service dispatching twice in quick succession
right after the minute rolled) runs `TryAcquire`, which correctly detects the stale window and
installs a **fresh** `Window(currentMinute, Count=1)` via `AddOrUpdate`. The first thread's `Prune()`
then executes its already-decided `TryRemove(key)` - which deletes whatever is *currently* stored,
i.e. the second thread's brand-new, still-current-minute counter, not the genuinely-stale value it
was reacting to. The next request for that key starts the window over at `Count=1` instead of
continuing it at `Count=2` - a silently lost increment. Under sustained concurrent traffic to one
identity/target, this lets more requests through per minute than `MaxPerMinutePerIdentity`/
`MaxPerMinutePerTarget` configure, precisely for the "stuck retry loop"/"compromised session" scenario
the limiter's own doc comment says it exists to bound.

**Verified** by reconstructing the interleaving deterministically (real thread races on a single-key
dictionary operation aren't reliably reproducible without an injected delay, so the exact internal
operation `Prune()` performs - reflection into the private `_windows` field, exactly as the existing
`WindowCount` helper in `MeshDispatchTest.cs` already does for other tests - was driven directly at
the moment the race would let it happen): `TryAcquire` charges the key twice across a simulated
minute rollover (`Count` should be 2 for the new minute after the second call), then the exact
operation `Prune()`'s loop body performs for an item it decided (from a pre-update snapshot) was
stale - `dict.Remove(key)` - is applied. Expected: the still-current window survives (`dict.Contains(key)`
stays true) and a subsequent `limit: 1` request is refused (the identity already made 2 this minute).
Actual: the entry is gone, and the subsequent `TryAcquire(key, limit: 1, ...)` call **succeeds**
(`Assert.False` fails) because the limiter's bookkeeping forgot the second charge ever happened.

The fix shape: use the conditional `TryRemove(KeyValuePair<TKey,TValue>)` overload (`.NET 5+`,
compare-and-remove on both key and value) instead of the unconditional two-argument overload, so a
stale decision from an enumeration snapshot can never delete a value that was concurrently replaced.

### 4. `MeshDispatchMessageHandler.HandleAsync`'s `NotImplemented` (no dispatcher registered for source) exit path never calls `Audit(...)`

`src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs:116-122`. Every other termination path in
this method - `gate-blocked`, `bad-request`, `not-found`, `rate-limited`, `dispatch-failed` (#186),
and the success path `dispatched` - calls `Audit(outcome, ...)`, leaving the "scoped, attributable
... call that leaves a record" trail the class's own doc comment calls out as the whole point of this
surface ("safer than handing someone a database credential" as "a property rather than an
assertion"). The one exception: when `entry` (the registered service) exists and the rate limiter
passes, but no `IMeshServiceDispatcher` matches `entry.Source` - the branch returns
`BenzeneResult.SetFailed<RawStringMessage>(BenzeneResultStatus.NotImplemented, ...)` directly, with no
`Audit` call at all.

This is exactly the failure mode most likely to occur in practice: a service gets registered in the
mesh registry with a given `Source` (e.g. `AwsLambdaInvoke`), but the matching dispatcher
(`AddMeshLambdaDispatcher()`) was never wired into the container - a routine misconfiguration right
after a deploy, not a hostile/adversarial input like the other branches. Every attempted dispatch to
that source silently vanishes from the audit trail while every other kind of failure against the same
target - wrong service name, rate-limited, or a genuine downstream failure - *is* recorded. An
operator debugging "why didn't my dispatch attempts show up in the audit log" for this one
misconfiguration class would find nothing, while every other failure mode leaves a trace.

**Verified** with a temporary test added to `MeshDispatchTest.cs`
(`NoDispatcherRegisteredForSource_StillLeavesAnAuditRecord`, alongside the existing
`NoDispatcherForSource_ReturnsNotImplemented` test that only checks the returned status, never the
audit log): a registry with `orders` registered but zero `IMeshServiceDispatcher`s supplied. Expected:
one `benzene.mesh.dispatch.audit` log entry, matching every sibling test for the other exit paths.
Actual: `Moq.MockException` - "Expected invocation on the mock once, but was 0 times... No invocations
performed."

### 5. `CompositeMeshFleetReadModel.TraceAsync`/`CorrelationAsync` swallow a genuine caller cancellation and misreport it as "not found"

`src/Benzene.Mesh.Collector/CompositeMeshFleetReadModel.cs:46-72`. Both methods wrap their call to the
injected `IMeshTraceSource` in a bare `catch { return null; }`, explicitly for fetch isolation ("a
failing trace source degrades a single trace/correlation lookup to 'not found' rather than throwing
out of the composite"). That's the right call for a genuinely failing backend (matching
`RecentFlowsAsync`/`TopicsFromUsageAsync`'s same pattern), but the bare `catch` also swallows an
`OperationCanceledException` raised because the *caller's own* `cancellationToken` (passed straight
through to `_traceSource.GetTraceAsync(traceId, cancellationToken)`) was cancelled - e.g. a
`mesh:query:trace`/`mesh:query:correlation` request wrapped in `UseTimeout(...)`, or an HTTP client
that disconnected mid-request. Rather than that cancellation propagating (as this codebase's own
documented convention requires elsewhere - `MessageHandler.cs`'s remark that "a genuine host
cancellation... is deliberately NOT converted into any of these [error results] - it propagates"),
the composite read model reports a false negative: the query "found nothing" rather than "was
cancelled". A caller/UI can't distinguish a cancelled request from an authoritative "that trace
doesn't exist", which is a meaningfully different signal for anything built on top (retry logic,
"trace not found, was it ever collected?" UX, etc.).

**Verified** with a temporary test added to `CompositeMeshFleetReadModelTest.cs`
(`TraceAsync_PropagatesRealCancellation_InsteadOfReportingNotFound`): a fake trace source whose
`GetTraceAsync` throws `new OperationCanceledException(cancellationToken)` when given an
already-cancelled token. Expected: `model.TraceAsync(...)` throws `OperationCanceledException`.
Actual: no exception - the call completes and returns `null`.

Lower severity than #1-4 above (it degrades gracefully rather than crashing or corrupting state, and
matches the same "bare catch" pattern already present in `Benzene.Mesh.Fleet.Aws.XRay/XRayTraceSource.cs`'s
`FetchBatchAsync`, so it's a systemic inconsistency across the trace-plane adapters rather than a
one-off), but flagged because it's the same underlying class of bug as #1 (a fetch-isolation `catch`
that doesn't distinguish "the backend failed" from "the caller cancelled") recurring at the layer one
level up from the trace sources themselves.

---

## Ruled out / re-confirmed intact

- `BoundedFanOut.WhenAllAsync`/`RunGatedAsync` itself: the semaphore-gating, ordering, and
  cancellation-of-queued-items behavior all read correctly and match their doc comments - the bug in
  finding #1 is in the callers' catch filters, not in `BoundedFanOut`.
- `HttpMeshTraceExporter`'s flush/batch/shutdown interaction (`src/Benzene.Mesh.Wire/IMeshTraceExporter.cs`):
  re-read end to end past #233's absolute-deadline fix. `DisposeAsync`'s idempotency
  (`Interlocked.Exchange`), the writer-complete-before-cancel ordering, the post-loop drain-and-flush,
  and `FlushAsync`'s unconditional `batch.Clear()` all correctly avoid both a lost-span and a
  double-export scenario. `Dispose()`'s synchronous bridge only bounds how long the *caller* waits (5s);
  it doesn't cancel an in-flight `FlushAsync`'s `PostAsync` call (which isn't linked to `_stopping.Token`
  at all), so on an unreachable collector the actual flush can keep running in the background past the
  5s the doc comment discusses - but this doesn't violate any documented guarantee (the guarantee is
  "shutdown doesn't hang", not "the pump stops instantly"), isn't a data-loss/duplication bug, and
  wasn't pursued further.
- `MeshDispatchMessageHandler`'s registry-check-before-rate-limit-charge ordering (#187) and
  cancellation-token plumbing (#185): both re-verified intact against their existing regression tests.
- `XRayTraceSource.EnrichRecentFlowsAsync`'s bare `catch { }` in `FetchBatchAsync`: also swallows a
  genuine cancellation (same class as finding #5), but X-Ray's batches run via a single `Task.WhenAll`
  with no per-item audit/log expectation and no rate-limiting-style state to corrupt, so the concrete
  consequence is limited to "a cancelled recent-flows enrichment silently degrades to summary-plane
  rows instead of propagating" - noted as the same systemic pattern as #5, not written up separately.
- `MeshCollectorStore.AddIssues`/`Register`: re-checked for the same null-*element*-inside-a-non-null-list
  gap finding #2 exposed for `AddEvents`. `AddIssues` already coalesces `incoming.ExemplarTraceIds`
  per-item but does not null-check `incoming` itself before use - however `MeshIssue` items inside
  `batch.Issues` come from the same wire shape as `MeshTraceEvent`, so a `null` element there would hit
  the identical bug; not independently reproduced (same root cause and fix as #2) but worth including
  in the same fix.
- `JaegerTraceSource.GetServicesAsync`'s `JsonException` handling, `ToTraceSummary`'s ordering/service-dedup,
  and `JaegerTraceMapper`'s span-to-event mapping: read correctly, no gap found.
