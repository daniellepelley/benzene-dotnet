# Rounds 12–14 (+ #47) fix plan (2026-08) — covers task board #47, #185–#196, #198–#202, #204–#223

**Status: READY FOR EXECUTION — not yet started.** This is the fix-design ruling doc for every
outstanding finding *older than round 15*: the round-12 review (`work/bug-fix-designs-round12-2026-08.md`),
the round-13 blind re-audit (`work/bug-fix-designs-round13-2026-08.md`), the round-14 review
(`work/bug-fix-designs-round14-2026-08.md`), and #47, the one long-pending pre-round-12 item. The
round-15 findings (#226–#244) have their own plan, `work/bug-fix-plan-round15-2026-08.md` (task #246);
the two plans are designed to be executable independently and mostly in parallel — the one known file
overlap is called out in WP-I below.

Follow the standard fix-round protocol (same as every prior round — stated in full in the round-15
plan's preamble; the short form):

- One isolated worktree per work package; **never `git stash`**; red-green tests reproducing the
  review's executed evidence, kept permanently as regression tests; per-WP `[RESOLVED] #NNN` entries
  appended to `work/outstanding-bugs.md`; `docs/capability-matrix.md` updated where behavior changes;
  commit locally only — the orchestrator merges sequentially and runs the central baseline
  (`Benzene.sln` Release build, `test/Benzene.Core.Test`, `test/Benzene.Mesh.Test`,
  `deploy/Mesh/Benzene.Mesh.Host.Test`, `Benzene.Examples.sln` build) after the final merge.
- Mark each task completed as its fix lands; on round completion, docs-archivist stamps and archives
  this plan plus the three review docs it covers (each doc agent in its own worktree).

The ten work packages below are file-disjoint from each other. Severity-first order if serialized:
WP-J (#198 is a one-bad-string-from-cache-wipe) → WP-G (#47) → WP-H (§dispatch audit gap) →
WP-K (Saga guarantee break) → WP-I → WP-L/WP-M/WP-N → WP-O → WP-P.

---

## WP-G — MeshAnnouncer self-disabling on a thrown start (#47)

**Files:** `src/Benzene.CloudService/MeshAnnouncer.cs`. Tests in the CloudService area of
`test/Benzene.Core.Test` (or wherever `MeshAnnouncer`'s existing tests live — grep first).

**Problem (verify against current source first — this is the oldest open item and the code has moved
since it was filed):** `EnsureStarted` (lines 56–73) flips `_started` to 1 via
`Interlocked.Exchange` *before* deriving the descriptor. The null-descriptor path now correctly
resets `_started` to 0 and retries on the next invocation — that half has been fixed since #47 was
filed. The residual gap: if `_descriptorSource.Get(resolver)` (or `TryGet()`) **throws**, the
exception propagates before any reset — `_started` stays 1 forever, the announcer never starts, and
worse, the exception escapes into the *invocation* that happened to trigger the lazy start —
violating both halves of the class's own documented contract ("every failure is swallowed and
retried on the next tick, and nothing here ever runs on, blocks, or fails an invocation").

**Fix:** wrap the descriptor derivation (line 63) in try/catch: on any exception, reset `_started`
to 0 and return without throwing (spec §6 — reduced mesh, never a service failure; the next
invocation retries). Keep the existing null-path reset as is. Consider a `Debug`-level log if the
class has a logger (it currently doesn't — don't add one just for this; the swallow-and-retry is the
documented design).

**Red test:** a `CloudServiceDescriptorSource` stub whose `Get` throws on the first call and
succeeds on the second: currently the first `EnsureStarted(resolver)` throws out to the caller and
the second call no-ops (announcer dead); after the fix, the first call returns quietly and the
second call starts the loop (assert `_runTask` activity via an observable fake `HttpClient` handler
receiving the register POST).

---

## WP-H — Mesh.Dispatch: cancellation, audit trail, limiter charging (#185, #186, #187)

**Files:** `src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs`,
`src/Benzene.Mesh.Dispatch/MeshDispatchRateLimiter.cs` (exact names per round-12 §1 — grep the
package). Tests in `test/Benzene.Mesh.Test`.

**#185 — fix:** replace the hardcoded `CancellationToken.None` in the dispatch call with a token
resolved via `ICancellationTokenAccessor` — the framework's established idiom, used by
`HttpBenzeneMessageClient` (copy its resolution pattern exactly). This makes `UseTimeout(...)`
wrapping `UseMeshDispatch()` actually bound the live dispatch.
**Red test:** the review's probe — `UseTimeout` around the dispatch handler with a slow mock
dispatcher: currently the dispatch runs to completion past the timeout; after the fix the dispatch
observes cancellation.

**#186 — ruling: audit-then-fail-as-result, never a silent raw throw.** Wrap the dispatcher call in
try/catch; on exception, call the same `Audit(...)` every other exit path uses (recording the target,
topic, caller identity, and the exception), then return the handler's established failure-result
shape (whatever vocabulary its other error exits use — match it; do not invent a new status). The
exception detail goes in the audit record and the result's error message, not an unhandled throw.
The package's own safety justification ("a scoped, attributable call that leaves a record") is the
requirement: a real production dispatch going wrong is exactly the moment the record must exist.
**Red test:** the review's probe — a throwing mock dispatcher: currently zero logger/audit
invocations and a raw exception; after the fix, exactly one audit entry containing the failure and a
failure result returned.

**#187 — ruling: validate before charging.** Reorder `MeshDispatchRateLimiter` so the registry
existence check runs **before** a per-target window is created/charged — an unregistered/arbitrary
service name is rejected without ever pinning a dictionary entry. That removes the unbounded-growth
vector at its source (500 garbage names = 0 entries), which is cleaner than adding pruning inside
the package. Keep the sibling middleware's external pruning untouched.
**Red test:** the review's probe — N distinct nonexistent service names through the limiter:
currently N permanent entries; after the fix, zero entries and each rejected with the
service-not-found outcome (not the rate-limited outcome).

---

## WP-I — Mesh Fleet: Tempo correlation fetch + Jaeger fan-out isolation (#188, #189, #190)

**Files:** `src/Benzene.Mesh.Fleet.Tempo/TempoTraceSource.cs` (+ its options class),
Jaeger's fan-out helper (round-12 calls it `BoundedFanOut` — **check whether this is Jaeger's own
private helper or the shared `src/Benzene.Core.Middleware/BoundedFanOut.cs`**; if it's the shared
class, this WP overlaps round-15 WP-C (#230), which adds a `CancellationToken` parameter to the same
file — in that case land WP-I *after* round-15 WP-C merges and build on its signature). Tests in
`test/Benzene.Mesh.Test`.

**#188 — fix:** rework `TempoTraceSource.GetCorrelationAsync`'s up-to-100-trace fetch loop to match
the pattern `Benzene.Mesh.Fleet.Aws.XRay` already gets right (round 12 verified it as "the correct
pattern the findings below are missing"): (a) **per-trace fetch isolation** — one trace's HTTP
failure is caught, logged, and skipped; every successfully-fetched trace is still returned;
(b) **bounded concurrency** with a `SearchConcurrency`-style option mirroring Jaeger's knob
(default modestly, e.g. 4 — match Jaeger's default).
**Red test:** the review's two probes — (a) a mid-loop failure among 6 traces currently discards the
whole search including the already-fetched 2; after the fix returns 5 of 6; (b) the latency probe's
max-concurrency measurement goes from 1 to the configured bound.

**#189 — fix:** give the Jaeger fan-out helper per-item isolation: collect each per-service task's
outcome individually (`try/catch` per item, or `Task.WhenAll` over tasks that each catch and record)
so one faulted service's task no longer discards the other services' completed results. Log the
failed service(s) and return the partial set — matching the composite read model's degradation
posture everywhere else.
**Red test:** two services, one throwing: currently the whole result is lost via `Task.WhenAll`
fault semantics; after the fix the healthy service's results are returned and the failure is logged.

**#190 — fix:** lift Tempo's hardcoded correlation search limit of 100 into an option
(`SearchLimit`, default 100 to preserve behavior) and log a warning when the result set hits the
limit — the exact pattern of Jaeger's `SearchLimitPerService` and X-Ray's #77-fixed logged warning.
**Red test:** limit-hit scenario logs the warning; configured limit is honored.

---

## WP-J — Cache + RateLimiting round-13 residue (#198, #199, #200, #201, #202)

**Files:** `src/Benzene.Cache.Redis/RedisCacheService.cs`,
`src/Benzene.Cache.Core/CacheWriteActions.cs` (+ read-side presence detection, likely
`CacheReadActions`/`CacheEntry` — grep for the `!string.IsNullOrEmpty(cacheValue)` check),
`src/Benzene.RateLimiting/Extensions.cs`, `src/Benzene.RateLimiting/RateLimitingMiddlewareBase.cs`
(the `ObjectDisposedException` catch from #143's fix). Tests in the existing RateLimiting/Cache test
areas of `test/Benzene.Core.Test`. Note: rounds 11 and 13 both touched these packages heavily — read
the `[RESOLVED]` entries for #133–#147 before changing anything, so no prior ruling is regressed.

**#198 — ruling: fail fast, never emit `"*"`.** `CreatePrefixActions`
(`RedisCacheService.cs:109-117`) must throw `ArgumentException` on a null/empty/whitespace prefix
before building the glob. A missing tenant id or unset config value becomes a loud startup/first-use
error instead of a one-string-away full-keyspace wipe. Also add a defense-in-depth guard in
`RedisWildcardActions.InvalidateEntryAsync` (or wherever the pattern is consumed): refuse to execute
a bare `"*"`/effectively-universal pattern.
**Red test:** the review's probe — `CreatePrefixActions(string.Empty)` and `("   ")` now throw;
a real prefix still produces the escaped `prefix*` pattern and invalidation still works.

**#199 — fix:** in `WriteThroughAsync`'s 3-arg overload (`CacheWriteActions.cs:61-94`), move the
caller-supplied `getCacheAction`/`getCacheValue` delegate invocations **inside** the same try/catch
protection `SyncCacheAfterWriteAsync` gives the cache I/O — the exact scope #139's fix established,
applied one call wider. A throwing delegate after a committed DB write must degrade to
"write succeeded, cache sync failed" (whatever #139's fix returns in that case — match it), never
surface as a failed request.
**Red test:** the review's probe — `getCacheAction` throwing after a successful DB write: currently
propagates as if the DB write failed; after the fix the result reports the write's success with the
cache-sync degradation.

**#200 — ruling: key the guard per-pipeline, as documented.** Round 11's #133 guard
(`UseInternallyOwnedRateLimiting`, `Extensions.cs:256-279`) is keyed on the shared
`IBenzeneServiceContainer`, but `MiddlewarePipelineBuilder<T>.Create<TNewContext>()` deliberately
shares one container across a service's sibling pipelines. Re-key the "one internally-owned limiter"
tracking on the **pipeline builder instance** (or an equivalent per-pipeline identity available at
registration time), so two sibling pipelines each get their own internally-owned limiter without
tripping the guard, while double-registration *within* one pipeline still fails fast. The docs and
exception text already say "per pipeline" — make the code match them, don't reword the docs. Take
care the per-pipeline key doesn't resurrect #133's disposal leak: each pipeline's limiter must still
be a container-owned factory singleton — use a keyed/named registration per pipeline if needed.
**Red test:** the review's probe — two pipelines off one container, each with its own
`UseFixedWindowRateLimiting`: currently the second throws `InvalidOperationException`; after the fix
both build and each pipeline's limiter operates independently (verify with distinct limits). Keep
the existing same-pipeline-double-registration fail-fast test green.

**#201 — ruling: presence = store-level existence, not string emptiness.** The negative-caching
presence check `!string.IsNullOrEmpty(cacheValue)` conflates "key absent" with "serializer emitted
an empty string". Change presence detection to `cacheValue != null` (a store miss is `null`; any
real stored value — including `""` — is a hit), verifying the Redis and in-memory read paths both
genuinely distinguish nil-from-store vs empty-value. If any store path can't make that distinction,
fall back to the sentinel-envelope approach (a fixed marker prefix for cached values) — but prefer
the null-vs-empty fix; it's smaller and serializer-agnostic.
**Red test:** the review's probe — an injected `ISerializer` that encodes `null` as `""`: currently
every cached-null is a permanent miss (cache penetration, #140's exact scenario); after the fix the
cached null is a hit and the DB delegate is not re-invoked.

**#202 — fix:** split #143's single `ObjectDisposedException` catch (around both the cost delegate
and `Acquire()`) into two: an ODE from the **cost delegate** is reported as a cost-delegate
dependency failure (still failing closed, per #134's ruling), while an ODE from **`Acquire()`**
keeps the "rate limiter has already been disposed" message. Preserve #143's behavior in every other
respect.
**Red test:** the review's probe — a cost delegate touching an unrelated disposed dependency:
currently mislabeled as a disposed limiter; after the fix the two messages are distinct (assert
both paths).

---

## WP-K — Saga: rollback on state-store failure + multi-failure surfacing (#208, #209)

**Files:** `src/Benzene.Saga/` (`Saga`/`SagaResult`/`RunAsync` internals — round 14 §2 names
`RunAsync` and `Compensate`; grep the package). Tests in the Saga area of `test/Benzene.Core.Test`.
Round 1's #15 concurrency fix lives here — re-run its tests after changing `RunAsync`.

**#208 — ruling: a state-store failure triggers compensation, not a raw abort.** When persisting
saga state throws after an effect-producing stage completed, catch it, run the registered
`Compensate` handlers for every completed stage (the same compensation path a step failure takes),
and return a result — `RolledBack` with the store exception attached, or `PartiallyRolledBack` if
any compensation also fails (populating the existing `CompensationFailures` list) — instead of
letting the store exception propagate raw out of `RunAsync`. The class's documented "all-or-nothing"
guarantee is the requirement; a state-store blip must not silently orphan real side effects.
Decide-and-document one edge: a state-store failure *before* any effect-producing stage completes
can keep today's throw (nothing to roll back) — note the choice in the XML doc.
**Red test:** the review's probe — a state store throwing immediately after a real stage completes:
currently the registered `Compensate` never runs and the exception escapes; after the fix the
compensation runs and the caller gets a rollback-status result carrying the store exception.

**#209 — fix:** add a `Failures` collection (list of the same shape `Failure`/`FailureException`
expose — step identity + exception) to `SagaResult`, populated with **every** failed step in the
failing stage. Keep `Failure`/`FailureException` as the first entry for backward compatibility, and
document them as convenience views over `Failures` — mirroring how `CompensationFailures` already
works.
**Red test:** the review's probe — two steps in one stage both failing concurrently: currently one
failure is surfaced and the other has no representation anywhere on the result; after the fix
`Failures.Count == 2` with both step identities present.

---

## WP-L — Autofac closed-generic routing (#210)

**Files:** `src/Benzene.Autofac/AutofacBenzeneServiceContainer.cs` (the six methods round 14
identified with the `IsGenericType` check). Tests alongside the existing Autofac adapter tests.

**Fix:** change the generic-routing check in all six methods from `IsGenericType` (true for open
*and* closed generics) to `IsGenericTypeDefinition` (true only for open generics), so a closed
generic `Type` takes the ordinary registration path — matching the Microsoft adapter, which round 15's
infra agent confirmed has no generic branching at all and handles both uniformly. Round 14 verified
the round-9 fixes (#82–87) under concurrency — re-run those tests after the change; the six call
sites are exactly the surface #82/#83 touched.
**Red test:** the review's probe — registering/resolving a closed generic handler type: currently
throws under Autofac, succeeds under Microsoft DI; after the fix both adapters behave identically
(assert side by side). An open generic registration must still take the generic path (keep existing
open-generic tests green).

---

## WP-M — CodeGen.ApiGateway/Markdown escaping + guards (#211, #212, #213)

**Files:** `src/Benzene.CodeGen.ApiGateway/ApiGatewayBuilderV1.cs` (+ wherever its YAML emission
lives), `src/Benzene.CodeGen.Markdown/MarkdownTypeBuilder.cs`. Tests alongside the round-9 #86/#87
regression tests (this WP is the direct continuation of that fix — read #87's `[RESOLVED]` entry
first).

**#211 — fix:** case-fold `Method` in `ApiGatewayBuilderV1`'s duplicate-route guard, exactly as the
production `ReflectionHttpEndpointFinder` it mirrors already does (that class has a comment about
this exact risk — copy its normalization). `"GET"` and `"get"` on the same path must collide in the
guard before ever reaching YAML emission.
**Red test:** the review's probe — two topics mapped to `"GET"` and `"get"` for one path: currently
passes the guard and emits duplicate `get:` keys (the #87 YAML shape); after the fix the guard
throws the same duplicate-route error identical casing gets.

**#212 — ruling: stop interpolating, emit through an escaper.** Route every user-controlled string
(topic names into `summary:`, path segments, header values) through a YAML-safe emission helper —
either a proper single-quoted-scalar escaper (double the internal `'`s, always quote) applied at
every interpolation site, or switch the affected blocks to a serializer-mediated emission like
`AsyncApiDocumentBuilder` uses (round 15 verified that builder clean for exactly this reason). The
round-15 plan's WP-F fixes the same bug class in Terraform (#244) — same principle, different
package; no shared code expected, but if you build a general YAML-escape helper somewhere shareable,
say so in the `[RESOLVED]` entry so WP-F can reuse it.
**Red test:** the review's probes — a `"` in a topic name (currently breaks the `summary:` scalar)
and a `: ` in a path segment (currently survives title-casing into an invalid unquoted sequence
item): both must yield YAML that a real YAML parser (add a dev-dependency parse step in the test)
loads without error, with the values intact.

**#213 — fix:** null-check `Items` in `MarkdownTypeBuilder.MapProperty` before dereferencing,
mirroring the sibling method in the same class that already guards the equivalent case; emit the
same placeholder that sibling produces.
**Red test:** an array schema with `Items == null` through the public method: currently NREs; after
the fix renders the placeholder.

---

## WP-N — S3 TestHelpers key encoding + ServiceBus client logger guard (#191, #192)

**Files:** `src/Benzene.Aws.Lambda.S3.TestHelpers/` (the `AsS3` builder),
`src/Benzene.Clients.Azure.ServiceBus/ServiceBusBenzeneMessageClient.cs` (+ sibling
`*BenzeneMessageClient`s sharing the pattern). (Same *packages* as round-15 WP-B's S3 DI extension
work but different files — no conflict.)

**#191 — fix:** encode `AsS3`'s fake object key exactly the way real S3 event notifications do
(URL-encoding where `+`, `%`, and other reserved characters are escaped — use the inverse of
`S3ObjectKeyCodec.Decode` so the pair round-trips by construction; if the codec has no `Encode`,
add one beside `Decode` and use it in both the helper and its tests). The fake must survive the
real production decode step added by #158's fix.
**Red test:** the review's probe — `"invoice+2024-08-27.pdf"` through `AsS3` and the real production
getters: currently comes back as `"invoice 2024-08-27.pdf"`; after the fix it round-trips intact.
Add `%`-containing and unicode keys to the same test.

**#192 — fix (minor):** `ArgumentNullException.ThrowIfNull(logger)` in the ctor — fail at
construction, not inside the failure-handling catch block at the worst possible moment. The review
notes every other `*BenzeneMessageClient` shares the pattern: sweep the siblings (grep the
constructor shape) and apply the same one-line guard family-wide in this WP, listing the touched
clients in the `[RESOLVED]` entry.
**Red test:** constructing with a null logger throws immediately; a normal construction with a
failing send still logs through the error path without throwing from the catch block.

---

## WP-O — Mesh UI: vendoring doc + upstream items (#204, #205, #206, #207)

**Files:** `src/Benzene.Mesh.Ui/CLAUDE.md` only. **The bundle itself must not be touched.**

**#204 — fix (the only in-repo change):** rewrite `Benzene.Mesh.Ui/CLAUDE.md` to state, first and
prominently: `mesh-ui.html`/`mesh-spec-ui.html` are a **minified React + Redux Toolkit build
vendored verbatim from the external `benzene-ui` repo**, kept in sync by the
`mesh-ui-drift-check.yml` CI job — **never hand-edit them**; changes are made upstream and
re-vendored. Then correct or delete the sections describing hand-written-vanilla-JS features and
conventions that the shipped bundle doesn't have. Keep whatever server-side guidance in the doc is
still accurate.

**#205, #206, #207 — ruling: upstream-only; do not fix here.** These are client-side behavior
changes (Refresh confirmation step, Sign-out pending/disabled state, explicit
`credentials:"same-origin"`) inside the vendored bundle — hand-editing it is exactly what the
drift-check exists to prevent, and any local edit would be overwritten on the next re-vendor. The
fixing agent should: (a) add a short "Known upstream items" section to the rewritten CLAUDE.md
listing the three, and (b) record all three in `work/outstanding-bugs.md` as
`[UPSTREAM] #205/#206/#207 — needs a change in benzene-ui + re-vendor` rather than `[RESOLVED]`.
Leave the three task-board entries **pending** with a note, or mark them completed only in the sense
"dispositioned as upstream" — orchestrator's call at merge time; record which was chosen. If the
`benzene-ui` repo is reachable in the execution environment, a follow-up task to fix them there may
be proposed to the user — but that is outside this repo's fix round.

---

## WP-P — Examples sweep (#193, #194, #195, #196, #214–#223)

**Files:** `Benzene.Examples.sln`, `examples/Cloudflare/worker/**`, `examples/K8sTransports/Domain/PlaceOrderMessageHandler.cs`,
`examples/GoogleCloudMesh/**`, `examples/CLAUDE.md`, `examples/Kafka/docker-compose.yaml`,
`examples/Asp/**`, `examples/App/**`, `examples/Outbox` (membership only). All independent,
example-local changes; one agent can take the whole set. Verification for this WP is
**build + run**, matching how rounds 12/14 found them: `Benzene.Examples.sln` must build clean at
the end, and each touched example should be run the way its README describes where feasible.

- **#214 — fix first (it's a build error):** `examples/GoogleCloudMesh/Mesh/Startup.cs:48` calls
  `MeshServiceRegistry.FromEnvironment()`, which doesn't exist; the example's own `MeshRegistry`
  class has it. Correct the call to the real class, then build the whole example solution the README
  claims builds.
- **#193 + #215 — fix:** add `examples/Cqrs`, `examples/K8sTransports`, and `examples/Outbox` to
  `Benzene.Examples.sln` (the documented build gate), so the standard verification step stops
  silently skipping them. Confirm all three build as members.
- **#216 — fix:** document `examples/GoogleCloudMesh` in `examples/CLAUDE.md` like every sibling
  mesh example (what it shows, how to build/run it).
- **#194 — fix:** bump `@cloudflare/containers` past the broken `0.0.15` (maintainer's own
  deprecation notice says `0.0.16+`); re-run the review's exact verification —
  `npm install` + `npx wrangler deploy --dry-run` — and require it to pass the local bundling step.
- **#195 — fix:** update `worker/wrangler.toml` to the current containers config shape, aligning it
  with the project's own `docs/getting-started-cloudflare.md` worked example (that doc is the
  source of truth here — the review found the example drifted from it, not vice versa). The same
  `wrangler deploy --dry-run` run must come back clean of the deprecated-shape warning.
- **#196 — fix:** correct the doc comment in `PlaceOrderMessageHandler.cs:23-25` to point at
  `App/Startup.cs` (where the referenced explanation actually lives).
- **#217 — fix:** pin `examples/Kafka/docker-compose.yaml`'s `confluentinc/cp-kafka` to the same
  last-ZooKeeper-compatible version the example's own test-harness compose file already pins
  (copy the exact tag from that file — it's the in-repo precedent).
- **#218 + #222 — fix:** remove the hardcoded Application Insights instrumentation key from
  `examples/Asp/Startup.cs:52` (read it from configuration with an empty/placeholder default and a
  comment) and delete the dummy DB connection string with the plaintext placeholder password from
  `examples/Asp/config.json` (nothing reads it — confirmed by round 14; delete rather than
  placeholder-ify).
- **#219 — fix:** make the demo JWT issuer's `Issuer`/`JwksUri` configuration-driven (defaulting to
  `http://localhost:5000/`) and add a README/comment note explaining the 401 symptom when the app
  runs on a different port.
- **#220 — ruling: delete** the orphaned `examples/App/Benzene.Examples.App.Data` project (referenced
  by nothing, stale pre-split namespace, out-of-support EF Core/Npgsql pin). Confirm zero references
  repo-wide before removal; note the deletion in the `[RESOLVED]` entry.
- **#221 — fix:** clear the CS8632 warning in `GoogleCloudMesh/Shared/MeshServiceWiring.cs`
  (add `#nullable enable` or drop the annotation — match the file's surroundings).
- **#223 — fix:** reorder `examples/Asp/Startup.cs`'s middleware to clear the ASP0001 warning
  (this file is copied verbatim by adopters — the warning must not ship in a template).

---

## Coordination with the round-15 plan (task #246)

Both plans can run in the same fix round. Known interactions:

1. **WP-I ↔ round-15 WP-C:** if Jaeger's fan-out helper turns out to be the shared
   `Benzene.Core.Middleware/BoundedFanOut.cs`, land WP-C first and build WP-I on its
   token-accepting signature (details in WP-I above).
2. **WP-M ↔ round-15 WP-F:** same escaping bug class in different packages; a shared YAML/HCL
   escaping helper is optional, not required — coordinate only if one is built.
3. **WP-N ↔ round-15 WP-B:** same S3 package, different files — no expected conflict; merge
   normally.
4. `work/outstanding-bugs.md` will conflict pairwise across *all* packages of both plans — the
   standard keep-both-sides marker-deletion resolution applies throughout.

On completion of both plans, every task from #185 through #244 (plus #47) should be completed or
explicitly dispositioned (`[UPSTREAM]` for #205–#207 per WP-O), the capability matrix current, and
all four review docs plus both plan docs archived.
