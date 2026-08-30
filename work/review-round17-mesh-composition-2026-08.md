# Round 17 - Mesh Composition Review (2026-08-30)

**Scope, per the brief:** not whether round 16's eight mesh work packages (#250-#256, landed in
`d552323` WP-E, `98d7df2` WP-F, `6b98852` WP-G) are individually correct - each ships its own
committed regression test and passes in isolation. This asks whether they **compose**: with each
other, and with the rest of the mesh (the versioned catalog meeting the query-cancellation fix, the
null-tolerance fix meeting the versioned catalog, the two adjacent cancellation-vs-failure fixes
meeting each other, the dispatch fixes meeting the auth/guard boundary, and the whole #250 chain
meeting `deploy/Mesh/Benzene.Mesh.Host`'s actual wiring).

**Method:** read every file round 16's WP-E/F/G touched plus every direct caller and callee, traced
each of the five scenarios the brief posed end to end, and where a scenario looked wrong, wrote a
small xUnit test against the real production types (not a re-implementation of the logic under test)
proving the concrete failure, ran it scoped to `test/Benzene.Mesh.Test`, then deleted it per the
read-only brief. Three findings cleared the bar; two of the five posed scenarios are ruled out with
reasoning below. All three findings are freshly discovered this round - none duplicate round 16's own
two findings (both already fixed by the commits under review here).

---

## Finding 1 - `RecordObservedActivityAndDrift` still checks only the "headline" version's descriptor: #251 fixed the multi-version false-drift bug in `HashMatches` and left an identical bug 90 lines below in the same file

**Where:** `src/Benzene.Mesh.Collector/MeshCollectorStore.cs` - `RecordObservedActivityAndDrift`
(lines ~591-627), which backs the collector's "Undeclared edge -> contract-drift" signal (spec §4.2).

**The composition gap.** #251 made `ServiceState` hold every live `(service, serviceVersion)`
descriptor (`Descriptors`, keyed by version) instead of one, specifically *because* a single
"current" descriptor made a healthy side-by-side deployment (v1 and v2 of `orders` both live) read as
contract drift once a newer version registered. `HashMatches` was correctly rewritten to check an
instance's hash against **every** live version's hash, not just the headline row - that part of the
fix is solid and is exactly what its own committed test (`MeshCollectorSideBySideVersionTest`)
proves.

But `RecordObservedActivityAndDrift` - the OTHER place the collector infers contract drift, driven by
real traffic rather than by hash comparison - was never updated. It still reads the single computed
`ServiceState.Descriptor` property:

```csharp
if (_services.TryGetValue(handler, out var handlerState) &&
    IsDeclared(handlerState.Descriptor, MeshDescriptorFactory.RegistryFeed) &&
    !ContainsTopic(handlerState.Descriptor!.Topics, key))
{
    FileContractDrift(handler, traceEvent);
}
```

`handlerState.Descriptor` is `Descriptors[CurrentVersionKey]` - the *most recently registered*
version only (see the type's own doc comment, added by #251 itself: "Older still-live versions
remain fully present in Descriptors ... they're just not the name-level 'headline' row"). A trace
event from an instance of an OLDER, still-live, correctly-registered version is checked against the
NEWER version's declared topics/produces. If the two versions declare different topics or produced
edges - the *entire point* of a canary/blue-green deployment, and the exact scenario #251's own
regression test exercises for `HashMatches` - every message the older version legitimately handles
gets misfiled as `contract-drift` the moment the newer version registers.

This is the identical false-positive #251 was written to eliminate, reintroduced one call path lower
in the same file, in the same commit.

**Red test (passes today, reproducing the gap), added and run under `test/Benzene.Mesh.Test/`, not
committed:**

```csharp
var v1 = Descriptor("orders", topics: new[] { "topic-a" }, serviceVersion: "1.0.0");
var v2 = Descriptor("orders", topics: new[] { "topic-b" }, serviceVersion: "2.0.0");
store.Register(v1);
store.Register(v2); // v2 becomes CurrentVersionKey - the "headline" row

// A v1 instance handles topic-a - exactly what v1's OWN live, registered descriptor declares.
store.AddEvents(new[] { Event("trace-1", "span-1", "orders", "topic-a", DateTimeOffset.UtcNow) });

Assert.Empty(store.Fleet().Issues); // FAILS: one contract-drift issue for "orders"/"topic-a"
```

```
Assert.Empty() Failure
Expected: <empty>
Actual:   [MeshIssue { Classification = "contract-drift", Count = 1, ..., Service = orders, Topic = topic-a }]
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 325 ms
```

**Why it matters:** this isn't a cosmetic false alarm. Contract-drift issues are exactly what
`ServiceSummaryLocked`'s `MissingFeeds`/the fleet issue inbox surface to an operator as "something is
wrong here" (spec §4.1). The first thing a canary rollout of `orders` would now do, the moment v2
registers, is manufacture a synthetic "orders is violating its own contract" issue for every message
v1 (the still-majority-traffic old version) legitimately handles - noise that undermines the exact
trust #251 was fixed to protect, on the exact deployment shape (#251's spec citation, §2.4) the round
was about.

**Assessment:** a real gap, not a style nit - #251's fix is *inconsistent within itself*: it updated
the hash-comparison signal to be version-aware but not the traffic-observed signal, even though both
exist for the same reason (telling a real drift from a healthy side-by-side deployment) and both read
from the exact same `ServiceState`. Fix shape (not applied): `RecordObservedActivityAndDrift` needs
either (a) `MeshTraceEvent` to carry the emitting instance's `ServiceVersion` so it can look up that
specific version's descriptor (the wire type doesn't today - see "Where I had to read source" below),
or (b), lacking that, to declare a topic "declared" if it appears in *any* live version's `Topics`/
`Produces` for the service, matching `HashMatches`'s "any live version" rule, accepting the coarser
approximation that a real single-version drift on an edge another version happens to also declare
would go undetected - a documented trade-off, not a silent false positive.

---

## Finding 2 - `CompositeMeshFleetReadModel.RecentFlowsAsync` never got #256's fix: the `mesh:query:fleet` path - the one #250 specifically wired real cancellation into - still swallows genuine caller cancellation and reports a normal, degraded response instead

**Where:** `src/Benzene.Mesh.Collector/CompositeMeshFleetReadModel.cs` - `RecentFlowsAsync` (private,
called from `FleetAsync`) and, for the same reason, `TopicsFromUsageAsync`.

**The composition gap.** #256's stated purpose is precise: "the fetch-isolation catch ... exists to
degrade a FAILING backend ... it must not also swallow a genuine cancellation of the caller's OWN
token." It applied that token-verified filter to exactly two methods, `TraceAsync` and
`CorrelationAsync`:

```csharp
catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
```

with two new committed tests (`TraceAsync_PropagatesRealCancellation_InsteadOfReportingNotFound`,
`CorrelationAsync_...`) proving it. `RecentFlowsAsync` - the method `FleetAsync` (i.e. `mesh:query:fleet`,
the single busiest query on the fleet UI, and the exact call `FleetQueryMessageHandler`'s #250 fix
just started threading a real ambient token into) - has a **bare** catch, unchanged since before #256:

```csharp
private async Task<List<TraceSummary>> RecentFlowsAsync(MeshTimeRange? range, CancellationToken cancellationToken)
{
    try
    {
        var flows = await _traceSource.GetRecentFlowsAsync(MaxFleetTraces, range, cancellationToken);
        return flows.ToList();
    }
    catch
    {
        return new List<TraceSummary>(); // swallows EVERYTHING, including the caller's own cancellation
    }
}
```

`TopicsFromUsageAsync`'s usage-source loop has the identical bare `catch { }`.

So: `#252` (WP-F) correctly taught `JaegerTraceSource`/`TempoTraceSource`/`XRayTraceSource` to let a
genuine caller cancellation escape their own per-item fan-out isolation rather than swallow it
alongside a per-item timeout - exactly the propagation `#256` then relies on one layer up in
`TraceAsync`/`CorrelationAsync`. But the SAME propagated cancellation, arriving at
`RecentFlowsAsync` via `FleetAsync`, is caught right back by the untouched bare `catch` and converted
into a normal, silently-degraded (empty-flows) success. One fix's careful work (#252, letting
cancellation out) is undone one call frame later by a sibling method #256 never touched.

**Red test (passes today, reproducing the gap):**

```csharp
var model = new CompositeMeshFleetReadModel(new CancellingTraceSource(), Array.Empty<IMeshUsageSource>());
using var cts = new CancellationTokenSource();
cts.Cancel();

await Assert.ThrowsAsync<OperationCanceledException>(() => model.FleetAsync(null, includeFlows: true, cts.Token));
```

```
Assert.Throws() Failure
Expected: typeof(System.OperationCanceledException)
Actual:   (No exception was thrown)
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 20 ms
```

(`CancellingTraceSource` is the exact fake `CompositeMeshFleetReadModelTest.cs` already defines and
uses for the passing `TraceAsync_PropagatesRealCancellation_...` test - this is the same fake, the
same assertion shape, aimed one caller frame higher, at the method that test class never covers.)

**Why it matters, concretely:** `TimeoutMiddleware` (`src/Benzene.Resilience/TimeoutMiddleware.cs`)
translates a downstream `OperationCanceledException` into `BenzeneResultStatus.Timeout` **only if one
is thrown**. If `next()` completes normally - which is exactly what happens here, because
`RecentFlowsAsync`'s bare catch converts the cancellation into a normal return - `TimeoutMiddleware`
sees a completed call and does nothing; the caller gets a 200 OK `FleetView` with empty
`Traces`/`Services`, not a timeout result. Wrap `mesh:query:fleet` against a trace-backed
(X-Ray/Jaeger/Tempo) composite plane in `UseTimeout(...)` and every deadline that fires reads to the
caller as "the fleet has no recent traffic" rather than "this timed out" - silently misleading, on
the exact plane and exact query round 16's own Finding 1 (`work/review-round16-mesh-composition-2026-08.md`)
was about. #250 fixed the plumbing that carries a real token down to this call; #256 established the
correct catch shape one layer above it; neither one reaches this specific method, so the abandoned-query
cost story round 16 diagnosed is, for the flows/services slice of a composite-plane fleet query, still
exactly as it was.

**Assessment:** a genuine, evidenced gap between two of round 16's own fixes (#250 delivering a live
token to a call site, #256 establishing the filter that call site needed but didn't get). Fix shape
(not applied): apply the same token-verified `catch` filter #256 used to `RecentFlowsAsync` and
`TopicsFromUsageAsync`.

---

## Finding 3 - `deploy/Mesh/Benzene.Mesh.Host`'s HTTP transport never threads a real cancellation token into the `mesh:query:*`/`mesh:dispatch` envelope's DI scope at all: #250 (and #185 before it) is correct and unit-tested in the library, and inert in the shipped host

**Where:** `src/Benzene.Http/BenzeneMessage/BenzeneMessageHttpMiddleware.cs` (`DispatchAsync`, line
137-138) and `src/Benzene.Core.Middleware/MiddlewareApplication.cs` (both generic
`MiddlewareApplication<...>` classes' 2-argument `HandleAsync` overload). Consumed by
`deploy/Mesh/Benzene.Mesh.Host/Startup.cs` for BOTH `mesh:query:*` (`asp.UseBenzeneMessage(...,
fleet => fleet.UseMessageHandlers(MeshCollectorHandlers.Queries))`, line 384-385) and `mesh:dispatch`
(the identical pattern, line 416-423, explicitly commented as "mirroring the fleet-query plane
above").

**The composition gap.** This is question 5 from the brief, and the answer is sharper than "the DI
registration is missing" - the registration (`services.TryAddScoped<CancellationTokenAccessor>()` /
`ICancellationTokenAccessor`, `Benzene.Core.MessageHandlers/DI/Extensions.cs:113-114`, universal to
every `AddBenzene()` container) is present and resolves fine. The break is one level up, in how the
real HTTP request's cancellation ever gets *into* that accessor for the scope the query/dispatch
handler actually runs in:

1. The OUTER `asp` HTTP pipeline (`BenzeneExtensions.BuildHttpPipeline`) runs a
   `"SeedCancellationToken"` middleware that sets `context.HttpContext.RequestAborted` on **that
   pipeline's own** `CancellationTokenAccessor` instance, in **that pipeline's own** per-request DI
   scope. This part works, and is what health checks and any handler mounted directly on `asp` rely
   on.
2. `asp.UseBenzeneMessage(...)` (the `mesh:query:*`/`mesh:dispatch` envelope) is a *nested* pipeline.
   Dispatching into it goes through `BenzeneMessageHttpMiddleware<TContext>.DispatchAsync`, which
   calls:
   ```csharp
   return await _application.HandleAsync(benzeneMessageRequest!,
       _serviceResolver.GetService<IServiceResolverFactory>());
   ```
   the **2-argument** overload of `IMiddlewareApplication<TEvent, TResult>.HandleAsync`.
3. That overload is defined, in both `MiddlewareApplication` classes, as:
   ```csharp
   public Task<TResult> HandleAsync(TEvent @event, IServiceResolverFactory serviceResolverFactory)
       => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);
   ```
   - it hardcodes `CancellationToken.None`, then creates a **brand-new** DI scope
   (`serviceResolverFactory.CreateScope()`) for the inner pipeline and seeds `CancellationToken.None`
   into *that* scope's own, freshly-constructed `CancellationTokenAccessor` instance (a distinct
   object from the outer pipeline's).

So `FleetQueryMessageHandler`/`ServiceQueryMessageHandler`/.../`MeshDispatchMessageHandler` - even
after correctly resolving `ICancellationTokenAccessor` "at the point of use" per #250/#185 - always
observe a token that was **never connected to the real HTTP request** on this transport, because the
transport that carries the request into their scope structurally discards it before the scope even
exists. This is true for every host built on `Benzene.Http`'s `UseBenzeneMessage` HTTP-envelope
pattern - not unique to this host, but this IS how `deploy/Mesh/Benzene.Mesh.Host` (and, per its own
comments, every `AwsMesh`/`AzureMesh`/`GoogleCloudMesh` example) mounts both endpoints the brief asked
about.

**Why round 16's/round 15's own regression tests didn't catch it:** `MeshDispatchTest`'s
`UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall` (the test #250's own commit
message says it "mirrors") constructs a **single, hand-shared** `CancellationTokenAccessor` instance
and passes it directly into both `TimeoutMiddleware` and `MeshDispatchMessageHandler`'s constructors.
That proves the resolve-and-thread pattern works correctly *if the same accessor instance is shared* -
it cannot, by construction, detect that the real host does not share it, because the test never goes
through `IServiceResolverFactory.CreateScope()`/`BenzeneMessageHttpMiddleware` at all.

**Red test (passes today, reproducing the gap), built on the SAME harness the repo's own
`MeshQueriesRoutingTest.cs` already uses to pin real query routing (`MicrosoftBenzeneServiceContainer`
+ `BenzeneMessageApplication`, the actual production types, not a reimplementation):**

```csharp
var container = new MicrosoftBenzeneServiceContainer(services);
container.AddBenzene().AddBenzeneMessage();
var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
pipelineBuilder.UseMessageHandlers(MeshCollectorHandlers.Queries);
var application = new BenzeneMessageApplication(pipelineBuilder.Build());
var rootFactory = container.CreateServiceResolverFactory();

// Simulate the real outer HTTP pipeline's per-request scope, already seeded by
// BenzeneExtensions.BuildHttpPipeline's "SeedCancellationToken" middleware from a genuinely
// cancelled HttpContext.RequestAborted (browser tab closed mid-poll).
using var outerRequestScope = rootFactory.CreateScope();
var outerAccessor = (CancellationTokenAccessor)outerRequestScope.GetService<ICancellationTokenAccessor>();
using var cts = new CancellationTokenSource();
cts.Cancel();
outerAccessor.CancellationToken = cts.Token;

// Exactly what BenzeneMessageHttpMiddleware<TContext>.DispatchAsync does.
var outerScopedFactory = outerRequestScope.GetService<IServiceResolverFactory>();
var response = (BenzeneMessageResponse)await application.HandleAsync(
    new BenzeneMessageRequest { Topic = "benzene:mesh:query:fleet", Body = "{}" }, outerScopedFactory);

Assert.Equal("ok", response.StatusCode);                          // routed and answered normally
Assert.NotNull(readModel.Observed);
Assert.False(readModel.Observed!.Value.IsCancellationRequested);  // PASSES - proves the bug
```

```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 514 ms
```

**What is NOT broken, for precision:** if an operator wraps `UseTimeout(...)` *inside* the same inner
envelope callback (e.g. `fleet.UseTimeout(TimeSpan.FromSeconds(5)).UseMessageHandlers(...)` -
`deploy/Mesh/Benzene.Mesh.Host` does not do this today, but could), that composition DOES work: the
timeout middleware and the query handler are constructed from the SAME single scope for that one
pipeline execution, so a service-side deadline correctly bounds the call. What is broken is
specifically the thing round 16's own Finding 1 named as the cost driver - a genuine, external caller
cancellation (a disconnected browser, a load balancer idle timeout) - which never reaches the inner
scope at all on this transport, regardless of any fix inside `Benzene.Mesh.Collector`.

**Assessment:** this is the most consequential finding of the round. #250 is a correct, well-tested
library-level fix that mirrors an established idiom (#185) exactly - and #185 has exactly the same
blast radius, for exactly the same reason, in the exact same shipped host (`mesh:dispatch` uses the
identical `UseBenzeneMessage` mounting one function down in `Startup.cs`). Neither #250 nor #185's
original fix touched `Benzene.Http`/`Benzene.Core.Middleware`, so neither could have closed this gap;
it sat one architectural layer below where either PR was looking. Fix shape (not applied):
`BenzeneMessageHttpMiddleware<TContext>.DispatchAsync` needs a real cancellation source (this
transport's `TContext : IHttpContext` doesn't expose one generically today - see "Where I had to read
source" below) to call the 3-argument `HandleAsync(event, factory, cancellationToken)` overload that
already exists and already does the right thing once given a real token.

---

## Ruled out (traced, not written up)

- **Q2 - `#253`'s null-element skip meets `#251`'s versioned catalog.** Traced `AddEvents`/`AddIssues`
  fully: neither method has, or ever had, any per-version indexing or parallel-array bookkeeping that
  a skipped index could misalign. Each loop iteration is fully self-contained (ring slot, span-service
  map, per-topic stats, per-service cumulative stats - all keyed off the surviving event's own fields,
  never off loop position), and `MeshTraceEvent`/`MeshIssue` carry no `ServiceVersion` field at all -
  `ServiceState.Invocations`/`Errors` are, and always were, aggregated across every version of a
  service, not attributed per-version. The hypothesized "index misalignment charges the wrong
  version" bug cannot occur because there is no per-version attribution point in this path to
  misalign. (Finding 1 above IS a real version-attribution bug in the same file, but it lives in
  `RecordObservedActivityAndDrift`, not in the null-skip loop `#253` touched, and is not caused by
  `#253`.)
- **Q4 - `#254`'s rate-limiter fix meets `MeshDispatchGuardMiddleware`'s own checks.** Read
  `MeshDispatchRateLimiter.cs`, `MeshDispatchGuardMiddleware.cs`, and `MeshDispatchMessageHandler.cs`
  in full. The guard middleware charges `"identity:{email}"`; the handler separately charges
  `"target:{service}"` - disjoint key namespaces by construction (a concatenated prefix collision
  would require an email literally equal to `"target:" + <a different string>`, which produces a
  different concatenated key regardless), so there is no double-counting between the two checks.
  `Prune()` (now CAS-safe per #254) operates on the whole shared dictionary regardless of which key
  prefix triggered it, so it stays correct even though only the guard middleware calls it. The
  existence-before-charge ordering `#187` established (reject an unregistered service before ever
  touching the target window) is untouched by `#254`/`#255`. No ordering or double-counting issue
  found.

## Build/test note

All three red tests were run scoped to `test/Benzene.Mesh.Test/Benzene.Mesh.Test.csproj -c Release
--filter "FullyQualifiedName~<TestClass>"`, not a full-repo build, per the brief's request to keep
builds scoped given other review agents running concurrently on this host. All three test files were
deleted after confirming their failure/pass mode; none were committed, and no source file under
`src/`/`deploy/` was modified.

## Where I had to read source

This was a source-level review by the brief's own design (not a UI walkthrough), so this section
records the two places reading source revealed something the wire contract/spec alone would not have:

- `MeshTraceEvent` (`src/Benzene.Mesh.Wire/MeshTraceEvent.cs`) has no `ServiceVersion` field. This is
  what makes Finding 1's fix non-trivial - there is no clean per-version fix available at the
  trace-ingestion layer without a wire-shape change, only the coarser "any live version declares it"
  approximation.
- `IHttpContext`/`TContext` in `Benzene.Http` (used by `BenzeneMessageHttpMiddleware<TContext>`) has
  no generic way to obtain a transport cancellation token - only the ASP.NET Core-specific
  `AspNetContext.HttpContext.RequestAborted` (read directly, non-generically, by
  `BenzeneExtensions.BuildHttpPipeline`'s `SeedCancellationToken` middleware for the OUTER pipeline).
  This is why the fix for Finding 3 isn't a one-line change to `BenzeneMessageHttpMiddleware` - the
  transport-neutral abstraction the class is written against doesn't expose the signal it would need
  to thread through.
