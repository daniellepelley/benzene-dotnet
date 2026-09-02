# Round 18 — Mesh dispatch, collector, tracing, and the OIDC auth gate (2026-08)

Scope: `Benzene.Mesh.Dispatch`, `Benzene.Mesh.Collector`, `Benzene.Mesh.Auth.Oidc`,
`Benzene.Mesh.Tracing.Tempo`, `Benzene.Mesh.Fleet.Jaeger`, `Benzene.Mesh.Fleet.Tempo`,
`Benzene.Mesh.Usage.ApplicationInsights`. Reviewed at commit `7f642b2` on `main`. Continues round 17's
auth-security pass (`work/review-round17-auth-security-2026-08.md`) and rounds 12-13/16-17's
Dispatch/Collector/Fleet hardening work, all confirmed still in place (WP-1, WP-B/G/H/I/J/K of rounds
12-17). No dotnet SDK available; every finding below was traced by hand, cross-checked against the
codebase's own extensive test-naming conventions to describe the regression test each would need.

**Method:** read every source file in the seven packages end to end, cross-referenced against
`work/outstanding-bugs.md`'s already-fixed items so nothing already tracked is re-reported, and applied
the brief's specific asks: can a caller reach a dispatch target around the authorization gate; are the
OIDC constant-time comparisons and token-trust checks actually sound (re-verified, not just re-read);
and is the Tempo/Jaeger fan-out isolation and bounded concurrency genuinely correct now.

**Headline result:** `Benzene.Mesh.Dispatch`'s authorization gate (`MeshDispatchGuardMiddleware` +
`MeshAuthGate`'s `dispatchRole` check) and `Benzene.Mesh.Auth.Oidc`'s CSRF/token-trust/open-redirect
machinery are both still sound — no bypass could be constructed in either, consistent with round 17's
conclusion. What this round found instead are two genuine unbounded-growth/missing-cancellation defects
in `Benzene.Mesh.Collector` and `Benzene.Mesh.Tracing.Tempo`, both real operational bugs with concrete
failure scenarios, plus two lower-severity items in the fan-out/time-window layer.

---

## Findings

### 1. MEDIUM — `MeshCollectorStore` leaks memory forever on three collections `#290`'s own fix (in the same file) proves the maintainers already recognize this defect class

`src/Benzene.Mesh.Collector/MeshCollectorStore.cs`

Round 17's `#290` (see `work/outstanding-bugs.md`) fixed exactly this defect for one collection in this
file — `ServiceState.Descriptors` (per-service version count) had "no eviction policy at all: a service
that legitimately re-registers under a new `ServiceVersion` on every deploy ... accumulated one
permanent entry per historical deploy for the entire life of the collector process." A
`maxVersionsPerService` cap (default 8) with least-recently-registered eviction was added.

That fix did not touch four other collections in the same class, all still fully unbounded for the life
of the process:

- **`_services`** (`MeshCollectorStore.cs:25`, grown by `EnsureService`, `MeshCollectorStore.cs:605-613`)
  — one entry per distinct service NAME ever seen, never evicted. A service that is decommissioned (its
  name retired) stays in this dictionary forever.
- **`_topics`** (`:26`, grown by `EnsureTopic`, `:636-644`) — one entry per distinct `(topicId, version)`
  pair ever seen, never evicted.
- **`ServiceState.Instances`** (`:81`, grown by `Heartbeat`, `:258-271`) — one entry per distinct
  `InstanceId` ever heartbeated for that service, never evicted, and **there is no eviction anywhere in
  this file for `Instances`** (confirmed: `grep -n "Instances\.\(Remove\|Clear\)"` over the whole package
  returns nothing).
- **`_providerActivity` / `_consumerActivity`** (`:38-39`, grown by `EnsureActivity`, `:709-718`) — one
  entry per distinct `(topic, caller-or-handler-service)` pair ever observed in a trace, never evicted.

**Concrete, non-adversarial failure scenario (no attacker required):** `examples/K8sMesh` wires a plain
`AddSingleton<MeshCollectorStore>()` (`examples/K8sMesh/Mesh/Startup.cs:80`) with no additional pruning
— this is the shipped, documented way to run the collector long-lived against a real Kubernetes fleet.
Kubernetes pods churn identity constantly under ordinary operation: every rolling deploy, HPA scale
event, or node eviction gives a pod a **new** name/instance id, and each one calls `mesh:heartbeat` with
that new `InstanceId`. Over the life of a long-running collector process (this is explicitly meant to be
long-lived — `MeshCollectorStore.StartedAtUtc`'s own doc calls the counts "cumulative since process
start"), `ServiceState.Instances` accumulates one permanent entry per pod that has ever existed for that
service, without bound. A service redeployed daily with 10 replicas accumulates 3,650 dead instance
entries after a year with zero traffic increase to explain it — pure leak. The same growth pattern hits
`_services` for any environment that creates and retires service names over time (dev/preview
environments, blue-green service renames), and `_providerActivity`/`_consumerActivity` for any topic
that is ever called by a service whose name later changes or that is retired.

**Adversarial amplification:** the shipped `deploy/Mesh/Benzene.Mesh.Host`'s ingestion endpoint
(`/mesh/report`, which carries `mesh:register`/`heartbeat`/`traces`/`issues`) defaults to
`auth.ingestion.mode: "open"` — **no authentication at all** — documented as a deliberate choice
("today's behaviour, no check — preserves the local-dev/demo experience",
`deploy/Mesh/Benzene.Mesh.Host/MeshHostConfigSections.cs:254-258`) with `sharedSecret` as the opt-in
hardening. An operator who has not set `sharedSecret` (or an attacker on a network that can reach the
endpoint even with a leaked secret rotated out of scope for one bad actor) can accelerate this leak
deliberately and arbitrarily fast: send `mesh:heartbeat` with a fresh random `InstanceId` per request, or
`mesh:register`/`mesh:traces` with a fresh random service name per request, to grow `_services`/
`Instances`/`_providerActivity` without bound at whatever rate the network allows — an unauthenticated,
unbounded memory-exhaustion DoS against the collector process. This is a straightforward escalation of
the same open-ingestion posture round 5-6's WP-1 and round 17 already reasoned about for other
attack surfaces on this same host (the mesh path-traversal and SSRF fixes tracked as resolved in
`work/outstanding-bugs.md`), just not yet applied to the collector's own in-memory footprint.

**Secondary effect — this is also a performance regression, not just memory:** `HashMatches`,
`ServiceSummaryLocked`, and the per-service `Instances` view in `Service(name)` (`:504-514`) all iterate
`state.Instances.Values`, so an unbounded `Instances` dictionary degrades every subsequent
`mesh:query:service` call for that service name from O(live instances) to O(every instance that has ever
existed) — exactly the "O(1) comparison ... unboundedly-growing O(v) scan per query" degradation `#290`'s
own writeup already names as the harm for the sibling `Descriptors` case.

**Proportionate mitigation, matching the shape of the `#290` fix already in this file:** apply the same
kind of bound to the three collections above — e.g. a `maxInstancesPerService` cap on `Instances` with
stale-heartbeat-based eviction (the file already carries `LastHeartbeat` per instance, so a simple
"evict the oldest-heartbeated instance when over cap" policy needs no new state), and either a hard cap
with LRU eviction or a documented TTL sweep for `_services`/`_topics`/`_providerActivity`/
`_consumerActivity`. The `#290` writeup itself already flags an open maintainer question about
cap-vs-TTL policy for `Descriptors` (`work/outstanding-bugs.md`'s "[OPEN] Is a hard max-versions-per-
service cap the right retention policy") — that same decision needs to be made for these four
collections too, since right now none of them have *any* policy at all.

**Regression test this would need:** a deterministic growth test mirroring `#290`'s own
(`Register_ManyDistinctVersions_DescriptorsAreCappedAtTheConfiguredMax`) for each collection — e.g.
`Heartbeat_ManyDistinctInstanceIdsForOneService_InstancesAreCappedAtAConfiguredMax` (heartbeat 5,000
distinct instance ids for one service, assert `Service(name)!.Instances.Count` stays bounded rather than
growing to 5,000), and the equivalent for `_services`/`_topics` via `AddEvents`/`Register` with many
distinct service/topic names.

---

### 2. MEDIUM — `Benzene.Mesh.Tracing.Tempo` threads no `CancellationToken` anywhere; `.UseTimeout(...)` around `mesh:topology` is a complete no-op

`src/Benzene.Mesh.Tracing.Tempo/PrometheusQueryClient.cs`,
`src/Benzene.Mesh.Tracing.Tempo/TempoServiceGraphTopologyBuilder.cs`,
`src/Benzene.Mesh.Tracing.Tempo/TempoTopologyMessageHandler.cs`

This is the exact defect class this codebase has spent five review rounds hunting down and fixing
everywhere else — round 12's `#185` (`MeshDispatchMessageHandler`), round 16's `#250`
(`mesh:query:*` handlers), round 16's `#261` (every outbound AWS client), rounds 14-15's WP-C (a
dozen-plus transport middlewares), and round 17's `#285` (the HTTP envelope transport itself) all fixed
one instance each of "a handler/client hardcodes `CancellationToken.None` (or omits the parameter
entirely) instead of resolving the ambient `ICancellationTokenAccessor`, so `UseTimeout(...)` wrapping it
has zero effect on the real I/O." `grep -rn "CancellationToken" src/Benzene.Mesh.Tracing.Tempo/*.cs`
returns **nothing** — this package was never touched by any of that work.

Concretely:

- `PrometheusQueryClient.QueryAsync(prometheusUrl, promQl)` (`PrometheusQueryClient.cs:33-51`) takes no
  `CancellationToken` parameter at all and calls `_httpClient.GetAsync(url)` (`:36`) with none.
- `TempoServiceGraphTopologyBuilder.BuildAsync()` (`TempoServiceGraphTopologyBuilder.cs:39-76`) makes
  **five** sequential PromQL HTTP calls (`requestsPerMinute`, `failedPerMinute`, `p50`, `p95`, `p99`) via
  `RunQueryAsync` (`:110-121`), none of which can be cancelled.
- `TempoTopologyMessageHandler.HandleAsync(Void request)` (`TempoTopologyMessageHandler.cs:46-52`) — the
  `[Message("mesh:topology")]`/`[HttpEndpoint("POST", "/mesh/topology")]` handler a caller actually
  invokes — has no `ICancellationTokenAccessor` constructor dependency and no way to reach one down into
  `_builder.BuildAsync()`.

**Failure scenario:** a host wraps `mesh:topology` in `Benzene.Resilience`'s `.UseTimeout(...)` (the
documented, supported way to bound a slow downstream call throughout this codebase — see every one of
the cancellation-fix entries above for handlers this codebase considers first-class). If the configured
Prometheus/Tempo metrics-generator endpoint is slow or hung (a real operational condition — this is
exactly the kind of dependency `.UseTimeout(...)` exists to bound), the timeout fires on the ambient
token, but nothing downstream in this package ever observes it: the five sequential `HttpClient.GetAsync`
calls run to completion (or the default `HttpClient` timeout, ~100s) regardless, so the deadline is
silently ignored and the request stays in flight up to 5× the client's own timeout in the worst case
(five sequential calls, each independently slow). This is precisely the failure mode round 16's `#261`
writeup describes for the AWS clients it fixed: "so `UseTimeout(...)` ... was a silent no-op; a stalled
call ran until the AWS SDK's own default retry/socket timeout, not the configured deadline" — identical
shape, different package.

**Proportionate mitigation, matching the established idiom used everywhere else in this codebase:**

1. `PrometheusQueryClient.QueryAsync` gains a `CancellationToken cancellationToken = default` parameter,
   threaded into `_httpClient.GetAsync(url, cancellationToken)`.
2. `TempoServiceGraphTopologyBuilder.BuildAsync`/`RunQueryAsync`/`QueryPerMinuteAsync`/`QueryLatencyMsAsync`
   gain the same parameter, threaded through the five `RunQueryAsync` calls.
3. `TempoTopologyMessageHandler` resolves an optional `ICancellationTokenAccessor` (the exact
   constructor-optional idiom `MeshDispatchMessageHandler`/the `mesh:query:*` handlers already use — see
   `src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs:36,140` for the pattern to copy) and passes
   its token into `_builder.BuildAsync(token)`.

**Regression test this would need:** the same shape as the WP-C sweep's own tests (e.g.
`test/Benzene.Core.Test/Clients/Aws/OutboundClientCancellationTest.cs`) — wrap
`TempoTopologyMessageHandler` in a real `Benzene.Resilience.TimeoutMiddleware<TContext>` at a short
deadline (e.g. 50ms) around a mocked `HttpMessageHandler` that stalls for several seconds per call;
before the fix the handler runs the full multi-second delay regardless of the deadline, after the fix it
aborts near the deadline. A second, narrower test asserting the actual token instance (not
`It.IsAny<CancellationToken>()`) reaches `HttpClient.GetAsync`, matching this codebase's own stated
convention for these tests ("asserts the *actual* token instance reaches the mocked transport", per
`work/outstanding-bugs.md`'s WP-C entry).

---

### 3. LOW — `MeshTimeRangeResolver`'s week/month/year units use unchecked `long` multiplication, silently wrapping instead of honoring the file's own "unrepresentable input degrades to absent" contract

`src/Benzene.Mesh.Collector/MeshTimeRangeResolver.cs:110-145`

This file's own class remarks state the contract precisely: "an unparseable OR unrepresentable bound is
treated as absent... Two distinct overflow paths are covered." Both documented paths are real and
correctly handled for the `'s'`/`'m'`/`'h'`/`'d'` units, because `TimeSpan.FromSeconds/Minutes/Hours/Days`
take a `double` parameter — the multiplication by the unit's scale happens inside .NET's own
double-domain arithmetic, which correctly detects overflow and throws `OverflowException` (caught at
`:138-144`).

The `'w'`/`'M'`/`'y'` branches (`:132-134`) do not go through that path:

```csharp
'w' => TimeSpan.FromDays(n * 7),
'M' => TimeSpan.FromDays(n * 30),
'y' => TimeSpan.FromDays(n * 365),
```

`n` is a `long` (parsed at `:118`) and `7`/`30`/`365` are `int` literals, so `n * 7` etc. is evaluated in
**`long` arithmetic before** the result is widened to `double` for `FromDays`. This repo does not set
`<CheckForOverflowUnderflow>` anywhere (`Directory.Build.props`, `src/Directory.Build.props`), so this
multiplication is `unchecked` by default: it **silently wraps** on overflow rather than throwing, and the
wrapped `long` — not the true mathematical product — is what reaches `TimeSpan.FromDays`. If the wrapped
value happens to land back inside `TimeSpan`'s representable range, `FromDays` does **not** throw, and
the `OverflowException` catch at `:138` never fires — the query silently resolves to a *wrong but
valid-looking* window instead of "absent" as the file's own P5 contract requires.

**Concrete, worked proof (computed independently in Python, not just asserted):** solving
`7n ≡ 5 (mod 2^64)` for `n` in the `Int64` range gives `n = 2635249153387078803` — well within
`long.TryParse`'s accepted range (`long.MaxValue ≈ 9.22×10^18`). A caller-supplied
`MeshTimeRange.From = "now-2635249153387078803w"` — a query-side input reachable from any
`mesh:query:fleet`/`service`/`topic`/`correlation` request, no authentication bypass needed, just an
oversized-but-parseable integer — resolves via `ParseDuration` to `n * 7 = 2635249153387078803 * 7`,
which wraps in 64-bit two's-complement arithmetic to exactly `5`, so the whole expression resolves to
`now - 5 days` instead of being rejected as unrepresentable. The caller asking for an absurd,
overflow-scale window silently gets back a completely different, attacker-chosen 5-day window instead
of the "absent/unfiltered" result the class remarks promise for exactly this input shape. (The map
`n ↦ 7n mod 2^64` is a bijection on 64-bit words since 7 is odd/invertible mod `2^64`, so this is not a
coincidence specific to `5` — every possible target day-count is reachable by some in-range `n`, for
every one of the three affected units.)

**Impact is honestly low:** this cannot crash the process (no unhandled exception either way) and has no
authorization/privilege effect — the worst outcome is a read-model query silently answering a different,
attacker-influenced time window instead of the documented "unfiltered" fallback, which could mislead an
operator reading `mesh:query:*` output during an incident, or be used to make a windowed query return a
narrow, misleadingly-clean-looking slice of data on demand. It is included here because it is the one
concrete gap in a query-input-hardening effort (P5) that this same file's CLAUDE.md describes as already
closed for "unrepresentable" input, and because the fix is a one-line-per-branch correction.

**Proportionate mitigation:** switch the three multiplications to `checked` arithmetic (either
`checked(n * 7)` etc., wrapped in the existing `catch (OverflowException)`, or multiply in `double` from
the start — `TimeSpan.FromDays((double)n * 7)` — which routes back through .NET's own correctly-checked
`Interval` helper the `'d'` branch already benefits from).

**Regression test this would need:**
`ParseDuration_LargeWeekCountThatWrapsInLongArithmetic_TreatedAsAbsent_NotSilentlyResolvedToAShortWindow`
— assert `MeshTimeRangeResolver.Resolve(new MeshTimeRange { From = "now-2635249153387078803w" }, now)`
returns `null` (absent), not `(now - TimeSpan.FromDays(5), now)`; parallel cases for the `'M'`/`'y'`
units with their own wrap-to-small-value magic numbers.

---

### 4. LOW — `TempoTraceSource.GetCorrelationAsync`'s bounded fan-out doesn't thread the caller's `CancellationToken`, unlike its `JaegerTraceSource` sibling

`src/Benzene.Mesh.Fleet.Tempo/TempoTraceSource.cs:85-107` vs.
`src/Benzene.Mesh.Fleet.Jaeger/JaegerTraceSource.cs:121-152`

Both adapters fan out per-item HTTP fetches through the shared `Benzene.Core.Middleware.BoundedFanOut`,
capped by a `SearchConcurrency` option (round 12-13's `#188`/`#189` fixes, both still correctly present
and per-item-isolated on this pass). `BoundedFanOut.WhenAllAsync` takes an optional trailing
`CancellationToken cancellationToken = default` parameter whose own XML doc is explicit about what it
buys: "Observed by an item still queued behind `maxDegreeOfParallelism`'s concurrency gate — cancelling
it stops queued items from ever starting" (`src/Benzene.Core.Middleware/BoundedFanOut.cs:35-42`).

Jaeger's `SearchAcrossServicesAsync` passes it through correctly:

```csharp
}, _options.SearchConcurrency, cancellationToken);   // JaegerTraceSource.cs:152
```

Tempo's `GetCorrelationAsync` omits it on the equivalent call:

```csharp
}, _options.SearchConcurrency);                       // TempoTraceSource.cs:107
```

The per-item body still receives and honors the real `cancellationToken` (threaded into
`FetchTraceEventsAsync(match.TraceId, cancellationToken)` at `:89`), so this is **not** a hang — once an
item is dequeued from behind the `SemaphoreSlim` gate, its own HTTP call still observes the real
cancellation and aborts quickly. What is lost is specifically the documented "queued items never start
at all" behavior: with a small `SearchConcurrency` (default 8) and a large match count (up to
`CorrelationSearchLimit`, default 100), a caller cancellation arriving mid-search still causes every
remaining batch of queued items to be dequeued and begin (and then immediately self-cancel) one
`SemaphoreSlim` batch at a time, rather than the fan-out short-circuiting the moment the semaphore's own
`WaitAsync` observes the token. The unwind is still bounded and fast in practice (each dequeued item
cancels near-instantly), so this is a genuine but low-impact inconsistency between two otherwise
identically-shaped sibling implementations — not a hang, not a resource leak, just a missed piece of the
documented cancellation contract that the sibling package already gets right.

**Proportionate mitigation:** add `, cancellationToken` as the trailing argument to the
`BoundedFanOut.WhenAllAsync` call at `TempoTraceSource.cs:107`, matching Jaeger's call shape exactly.

**Regression test this would need:** mirror
`test/Benzene.Mesh.Test/TempoTraceSourceTest.cs`'s existing
`GetCorrelationAsync_FetchesMatchedTracesConcurrently_NotSequentially` shape — drive a search returning
more matches than `SearchConcurrency`, cancel the token immediately, and assert (via a counting mock
`HttpClient`) that the number of per-trace fetch attempts started is bounded by `SearchConcurrency`
(the items already in flight when cancelled) rather than growing with the match count.

---

## Packages reviewed with no findings

- **`Benzene.Mesh.Dispatch`** — the gate (`MeshDispatchGate`: fail-closed on unset/Production
  environment), the registry-before-rate-limit ordering (`#187a`), the audit-on-throw path (`#186`), the
  cancellation threading (`#185`), and the response-size cap with correct UTF-8-boundary-aware truncation
  (`#246`) are all present and correct as documented. `MeshDispatchIdentity` is correctly registered
  `TryAddScoped` in `Benzene.Mesh.Artifacts/MeshArtifactExtensions.cs:120` (a singleton registration here
  would have been a serious identity-leak-across-requests bug; it is not one). `MeshDispatchGuardMiddleware`
  (`Benzene.Mesh.Artifacts`) and `MeshAuthGate`'s `dispatchRole` check both route through the same shared
  `MeshPathCanonicalizer.IsPathOrTopicMatch` predicate since round 17's `#287`, so a caller cannot reach
  the dispatch handler via a route alias that only one of the two checks would catch — no bypass could be
  constructed.
- **`Benzene.Mesh.Auth.Oidc`** — re-verified round 17's specific claims rather than trusting them: the
  state-token double-submit CSRF check and the session-cookie signature check both use
  `CryptographicOperations.FixedTimeEquals` genuinely (not merely named "constant-time"); cross-token
  confusion between the state and session payloads (which share one signing key and an `Exp` field) is
  explicitly guarded on both sides; `email_verified`/`iss`/`aud` are checked in the single code path that
  trusts an ID token (`OidcIdTokenValidator.ValidateAsync`) and there is no second path that bypasses it;
  `ReturnToValidator.IsSafe` rejects every open-redirect shape tried (protocol-relative `//`/`/\`,
  embedded `://`, control-character header-injection bypass); the state and session cookies are cleared
  with matching `Path` scopes on every code path that sets or clears them. Both round 17 findings
  (`#286`/`#287`, the signing-key repeating-block check and the dispatch-role topic fallback) are
  confirmed fixed in current source.
- **`Benzene.Mesh.Usage.ApplicationInsights`** — cancellation is correctly threaded end to end
  (`FetchUsageAsync` → `QueryAsync` → `QueryWorkspaceAsync(..., cancellationToken: cancellationToken)`);
  the KQL injection defence-in-depth from round 7-10's `#78` (`EscapeKqlStringLiteral`, rejecting embedded
  line breaks) is present and correctly applied to all three configured dimension names.
- **`Benzene.Mesh.Fleet.Jaeger`** — per-service fetch isolation (`#189`) and bounded, cancellable
  concurrency are both correctly implemented (the one package of the two Fleet adapters that gets the
  `BoundedFanOut` cancellation-token argument right — see Finding 4 above for the Tempo-side gap).
- **`Benzene.Mesh.Collector`**'s remaining surface (`Handlers.cs`, `CompositeMeshFleetReadModel.cs`,
  `CollectorUsageSource.cs`) — the round 16-17 cancellation-propagation and fetch-isolation fixes
  (`#250`, `#284`) are confirmed present and correctly scoped (cancellation propagates as
  `OperationCanceledException`, a genuine backend failure degrades to empty/null, and the two are still
  correctly distinguished by the `!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)`
  filter in every fetch-isolation catch clause checked).

---

## Overall assessment

This territory is in materially better shape than most first-pass reviews find: the dispatch
authorization gate and the OIDC login flow — the two highest-consequence surfaces in scope — both held
up under a genuinely adversarial re-check, not just a re-read of prior findings. The four issues found
here are second-order: two are unbounded in-memory growth/missing-cancellation gaps that are instances of
defect classes this codebase has *already* found and fixed dozens of times elsewhere (unbounded
collector state per `#290`; missing cancellation threading per the WP-C sweep and `#250`/`#261`/`#285`),
just not yet swept into these three specific spots (`MeshCollectorStore`'s three other collections
alongside `Descriptors`; the whole `Benzene.Mesh.Tracing.Tempo` package). The other two are narrow,
low-impact inconsistencies with clear, cheap fixes. None of the four is a security bypass; the two MEDIUM
items are genuine availability/correctness bugs worth fixing before this deployment shape (a long-running,
possibly-open-ingestion collector process) sees more production traffic.
