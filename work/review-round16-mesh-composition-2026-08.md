# Round 16 - Mesh Composition Review (2026-08-30)

**Scope, per the brief:** not another pass over individual components (discovery #148-157, dispatch
#185-187, tracing #74-79/#188-190/#233, collector/schema #234-235, UI #36/#204-207, auth #172-182,
the `Benzene.Mesh.Host` wiring) - each of those has already been reviewed and fixed in isolation.
This round asks where pieces that are each *individually* correct still fail to *compose*: a
guarantee one layer makes that an adjacent layer, itself correctly fixed, no longer relies on or
silently drops.

**Method:** read the mesh subsystem end to end (collector -> read model -> trace sources -> message
handlers -> HTTP host; and separately, descriptor -> register -> heartbeat -> catalog -> display),
cross-referenced against `docs/specification/mesh.md` (the cross-language spec, read from the
sibling `Benzene` monorepo checkout - this repo only vendors the wire types, not the prose) and
against `work/outstanding-bugs.md`'s record of what previous rounds actually fixed, so each finding
below is checked against "was this already known and accepted" before being written up. Two
findings cleared the bar. Both are backed by a new, currently-passing regression test that
reproduces the composition gap directly (not a mock of the bug), added under `test/Benzene.Mesh.Test/`
and run in isolation, not committed, per the review's read-only brief. Several other candidate seams
were traced and ruled out; see "Ruled out" at the end.

---

## Finding 1 - The mesh:query:* handlers never resolve ICancellationTokenAccessor: every layer below them was built for cancellation, and it never arrives

**Where:** `src/Benzene.Mesh.Collector/Handlers.cs` (`FleetQueryMessageHandler`,
`ServiceQueryMessageHandler`, `TopicQueryMessageHandler`, `TraceQueryMessageHandler`,
`CorrelationQueryMessageHandler` - collectively `MeshCollectorHandlers.Queries`, the handler set both
`deploy/Mesh/Benzene.Mesh.Host/Startup.cs` and every composite-plane example deployment wire under
`/benzene/invoke`).

**The composition gap.** Trace a `mesh:query:fleet` request against a trace-backed (X-Ray/Jaeger/
Tempo) composite plane from the browser down:

- `IMeshFleetReadModel` (`src/Benzene.Mesh.Collector/IMeshFleetReadModel.cs`) was deliberately built
  cancellation-aware - every method takes `CancellationToken cancellationToken = default`, documented
  as such ("Async because a backend-composed reader does I/O").
- `CompositeMeshFleetReadModel` (`src/Benzene.Mesh.Collector/CompositeMeshFleetReadModel.cs`) threads
  that token into every downstream call: `_traceSource.GetRecentFlowsAsync(...)`,
  `.GetCorrelationAsync(...)`, `.GetTraceAsync(...)`, `source.FetchUsageAsync(...)` - real plumbing,
  not a placeholder parameter.
- `JaegerTraceSource`/`TempoTraceSource`/`XRayTraceSource` each honor it down to the HTTP call, and
  `JaegerTraceSource.SearchAcrossServicesAsync` feeds it into `BoundedFanOut.WhenAllAsync` - the exact
  call site round 15's **#230** fix (`src/Benzene.Core.Middleware/BoundedFanOut.cs`) was written and
  audited for, specifically named in that fix's own note as "had a real ambient token in scope and now
  passes it".
- **`FleetQueryMessageHandler.HandleAsync` (and its four siblings) never had a token to pass in the
  first place.** Their constructors take only `IMeshFleetReadModel`; every call site in `Handlers.cs`
  reads `await _readModel.FleetAsync(request.Window, request.IncludeFlows ?? true)` - no third
  argument - so the parameter's `= default` fires every single time, unconditionally.

This is exactly the established, tested idiom the codebase already has for this problem, sitting one
package over and unused: `MeshDispatchMessageHandler` (`src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs`,
fixed for **#185**) takes an optional `ICancellationTokenAccessor? cancellation`, resolves
`_cancellation?.CancellationToken ?? CancellationToken.None` "at the point of use", and
`MeshDispatchTest.UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall` proves
`UseTimeout(...)` wrapping the dispatch envelope actually bounds a slow real dispatch. `Startup.cs`
mounts the query envelope (`asp.UseBenzeneMessage(... fleet => fleet.UseMessageHandlers(MeshCollectorHandlers.Queries))`)
the identical way it mounts dispatch - an operator who wraps either envelope in `UseTimeout(...)`
(a documented, general Benzene composition, `src/Benzene.Resilience/Extensions.cs`) would reasonably
expect both to be bounded. Only dispatch is.

**Why it matters, concretely, not hypothetically:** this is the exact cost problem the mesh-drains-up
review's "COST ROUND" and "COST ROUND 2" entries (`work/archive/mesh-drains-up-review-2026-07.md`)
already diagnosed and partially fixed on the AWS X-Ray plane - `GetTraceSummaries` bills per trace
*scanned*, and the widest window (the 24h issue inbox) was the dominant cost driver. Those fixes
addressed the *query shape* (`IncludeFlows`, sampling, poll cadence). They did nothing for
*abandoned* queries: a browser tab closed mid-poll, a load balancer's idle timeout firing, or an
operator navigating away during a slow 24h X-Ray scan does not cancel anything downstream - the full
backend scan runs to completion and is paid for regardless, because `HttpContext.RequestAborted` never
reaches `IMeshFleetReadModel`. The plumbing that would prevent exactly this (seeded once, generically,
by `src/Benzene.AspNet.Core/BenzeneExtensions.cs`'s `SeedCancellationToken` middleware, for "any
component resolving `ICancellationTokenAccessor`") is already running in every mesh HTTP request; the
five query handlers simply never ask it a question.

**Red test (passes today, i.e. reproduces the gap):**
`test/Benzene.Mesh.Test/MeshCollectorQueryCancellationTest.cs` -
`UseTimeout_AroundAFleetQuery_DoesNotBoundTheRealReadModelCall` wraps a `FleetQueryMessageHandler`
backed by a 5-second-delay fake `IMeshFleetReadModel` in a 50ms `TimeoutMiddleware`, the same
construction `MeshDispatchTest` uses to *prove* the dispatch fix works. Here it proves the opposite:
the call runs the full ~5s regardless of the 50ms deadline, the fake never observes cancellation, and
it received `CancellationToken.None` verbatim.

```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 5 s - Benzene.Mesh.Test.dll (net10.0)
```

(A 5-second pass to prove a 50ms deadline had zero effect - the long duration IS the evidence, exactly
as the #185 regression test uses a 20s simulated dispatch against the same 50ms deadline for the same
reason.)

**Assessment:** every individual layer here is correct and was fixed correctly (#230's audit was
real and thorough for the call sites it walked). The composition gap is that the audit's scope was
"every `BoundedFanOut.WhenAllAsync` call site", not "every path a real HTTP request can take to reach
one" - and the one path that matters most for cost (an aborted browser query against a trace-backed
plane) starts one layer higher, in a package #230 never touched. Fix shape (not applied, per the
read-only brief): give the five query handlers the same optional `ICancellationTokenAccessor?`
constructor parameter and pass its `.CancellationToken` through, mirroring
`MeshDispatchMessageHandler` exactly.

---

## Finding 2 - MeshCollectorStore has no slot for more than one live (service, serviceVersion): a legitimate side-by-side deployment is reported as contract drift

**Where:** `src/Benzene.Mesh.Collector/MeshCollectorStore.cs` - `EnsureService(string name)` (keys
`_services` by service name only), `Register(MeshServiceDescriptor descriptor)` (unconditionally
overwrites `state.Descriptor` wholesale), and `Service(string name, ...)`'s `HashMatches` computation.

**The composition gap.** `docs/specification/mesh.md` section 2.4 is explicit and unambiguous on this
exact scenario:

> A collector's catalog key is the pair `(service, serviceVersion)`, so two releases deployed side by
> side are two catalog entries rather than one silently overwriting the other.
>
> A re-registration of the same pair with a different `descriptorHash` is contract drift... Two
> *different* versions reporting different hashes is **not** drift; it is the expected state of a
> side-by-side deployment.

Every layer that produces the inputs to this rule is correctly built:

- `MeshDescriptorFactory.Create` (`src/Benzene.Mesh.Wire/MeshDescriptorFactory.cs`) stamps a real,
  spec-correct `ServiceVersion` and computes `DescriptorHash` per section 2.2's canonical-JSON rule.
- `MeshHeartbeat.DescriptorHash` correctly carries each instance's own descriptor hash back
  (`src/Benzene.Mesh.Wire/MeshTraceEvent.cs`).
- `ServiceView.Instances[].HashMatches` (`src/Benzene.Mesh.Collector/Views.cs`,
  `MeshCollectorStore.Service`) correctly compares an instance's reported hash against whatever
  `state.Descriptor.DescriptorHash` currently is.

But `_services` (`Dictionary<string, ServiceState>`) is keyed by `name` alone -
`EnsureService(descriptor.Service)` - and `ServiceState` holds exactly one `Descriptor`. There is no
`(service, serviceVersion)` composite key anywhere in the store; `ServiceSummary.ServiceVersion` /
`ServiceView.ServiceVersion` (`Views.cs`) are single scalar fields, not one per version. So:

1. A canary/blue-green rollout - v1 and v2 of `orders` both registered and both heartbeating, exactly
   the state section 2.4 exists to name as normal - collapses to one `ServiceState`. Whichever
   version's `Register` call landed last silently evicts the other's `Topics`/`Produces`/
   `DescriptorHash` from the catalog entirely.
2. Every instance still healthily heartbeating the *evicted* version now has its own, entirely correct
   descriptor hash compared against the *survivor's* descriptor, and `HashMatches` reports `false` -
   the exact "contract drift" signal section 2.4 says a side-by-side deployment must **not** produce.
   A real operator would read this as "this instance is running stale/wrong code" during a perfectly
   healthy canary.
3. The reverse direction is equally real: an actual drift (an instance silently redeployed with a
   changed contract *without* bumping `serviceVersion` - the one case `HashMatches: false` is supposed
   to mean) is indistinguishable on this plane from "this is just an older version's instance, ignore
   it" - the store has no way to know whether two differing hashes under one service name belong to
   one version or two.

**Red test (passes today, i.e. reproduces the gap):**
`test/Benzene.Mesh.Test/MeshCollectorSideBySideVersionTest.cs` -
`TwoSideBySideVersions_SecondRegistrationEvictsTheFirstsContractAndFalselyFlagsItAsDrift` registers
`orders@1.0.0` (producing `order:created:v1`) then `orders@2.0.0` (producing `order:created:v2`),
heartbeats one instance per version with each instance's own correctly-computed
`MeshDescriptorHashing.ComputeHash` value, then asserts against `MeshCollectorStore.Service("orders")`:

```
Assert.Equal("2.0.0", view!.ServiceVersion);       // v1's catalog entry is gone, not degraded - gone
Assert.False(v1Instance.HashMatches);              // v1's own, correct instance flagged as drifted
Assert.True(v2Instance.HashMatches);               // only the "winning" version reads healthy
```

```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 129 ms - Benzene.Mesh.Test.dll (net10.0)
```

**Assessment:** this is the strongest finding of the round because it is exactly the shape the brief
asked for - "does the mesh's own self-description stay internally consistent across a service's full
lifecycle (register -> announce -> discover -> aggregate -> display)". Each stage individually is
faithful to the spec; the storage model underneath all of them was apparently built against the
single-version case only (register replaces "the" descriptor) and never revisited when section 2.4's
versioning rules were written - `work/outstanding-bugs.md` shows no record of this being a named,
deferred gap (unlike, e.g., the AwsMesh composite-plane issue-store vessel, which *is* tracked as a
deliberate deferral in `work/archive/mesh-drains-up-review-2026-07.md`). Given how much of recent
work (WP-C/#230, WP-D/#233-235) is specifically about the collector's degradation contract, a silent
multi-version data-loss bug in the same file is a real gap, not a style nit. Fix shape (not applied):
key `_services` (or add a nested per-version map) by `(Service, ServiceVersion ?? "")`, and make
`Fleet()`/`Service()`/catalog rendering aware that one service name can now resolve to more than one
live version.

---

## Ruled out (traced, not written up)

- **`MeshPollBackgroundService.ExecuteAsync` calling `MeshAggregator.RunOnceAsync(_registry)` without
  `stoppingToken`.** `RunOnceAsync` has no `CancellationToken` parameter at all. Initially looked like
  the same class of bug as Finding 1, but `MeshAggregator`'s own `PerServiceFetchTimeout` (10s,
  documented, matches `Benzene.HealthChecks.TimeOutHealthCheck`'s convention) already bounds every
  per-service fetch, and fetches run concurrently (`Task.WhenAll`) - so a shutdown during a pass waits
  at most ~10s regardless, a deliberate, documented design choice (see the class's own remarks on the
  `_runGate` history), not an unbounded hang. Not written up as a finding.
- **`descriptorHash`'s inclusion of `Placement` (which carries `Region`).** Looked like a tension with
  section 2.2's "two instances of the same build MUST hash identically" if a build is deployed
  multi-region, but section 2.2 itself lists `placement` as a dimension the hash **must** change
  over - this is a spec-level design question (and the spec lives in the sibling `Benzene` monorepo,
  not this one), and the .NET implementation (`MeshDescriptorHashing.ComputeHash`) matches the spec's
  own stated rule exactly. Not a `benzene-dotnet` composition bug.
- **Jaeger vs. X-Ray vs. Tempo's differing "reachable but unsuccessful" idioms** (Jaeger's
  `GetStringOrNullAsync` maps a non-2xx response to `null`-then-empty; a connection failure still
  throws for the composite's fetch isolation to catch). This is explicit, commented, deliberate
  per-adapter behavior ("the topology adapter's 'one bad query shouldn't fault the build' rule"), and
  every path - null-degrade or thrown-and-caught - is absorbed identically by
  `CompositeMeshFleetReadModel`'s per-slice try/catch, so there is no externally observable
  inconsistency between the three planes. Not written up.
- **`MeshAuthGate` / `MeshDispatchGuardMiddleware` / the two-DI-container boundary** (the seam
  `Startup.cs`'s own comments spend the most words on). Read in full; the composition is unusually
  well-documented in-place (canonical path matching shared via `MeshDispatchGuardOptions.Path`,
  `context.User` deliberately used as the cross-container bridge instead of a scoped type, the #19/#37
  fail-fast validation matrix). No gap found beyond what rounds covering #172-182 already fixed.

## Build/test note

Both new tests were run scoped to `test/Benzene.Mesh.Test/Benzene.Mesh.Test.csproj` only
(`dotnet test ... --filter "FullyQualifiedName~<TestClass>"`), not a full-repo build, per the brief's
request to keep builds scoped given other review agents running concurrently. Neither test file was
committed; no source file was modified.
