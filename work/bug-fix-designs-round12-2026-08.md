# Round 12 review findings (2026-08)

**Status: ACTIVE — findings only; fix designs now ruled in [`bug-fix-rulings-round12-13-2026-08.md`](bug-fix-rulings-round12-13-2026-08.md) (#185–#196) — not yet implemented.** This round was scoped
review-only, continuing the round-11 pattern into the last genuinely fresh corners of the codebase:
4 parallel review agents, each in an isolated worktree detached at `c4086e8` (the head of `main`
after round 11's fix round fully landed and was archived), with a ~50-minute budget each. Findings
are tracked as task board **#185–#196** (7 worth-fixing, 5 minor), plus a round-summary task
**#197**.

Every finding below was **executed**, not just reasoned about: real mocked SDKs, real generated
test helpers driven through real production getters, a real `AWSXRayRecorder.Instance` singleton,
real `npm install`/`wrangler deploy --dry-run` runs against the actual Cloudflare tooling, and real
running example apps hit with real HTTP requests. Each agent cross-checked its findings against
`work/outstanding-bugs.md` and `work/archive/*.md` before reporting, and confirmed a clean baseline
(`dotnet build Benzene.sln -c Release` → 0 errors, `git status` clean) before and after probing.

## Why this ground was picked

A quick inventory before this round showed `src/` alone has 178 packages, and only 10 had never
been named in any of the prior 11 rounds' findings or decision docs. This round targeted the
remainder of that genuinely-untouched set (`Benzene.Mesh.Dispatch`, `Benzene.Mesh.GoogleCloud.Storage`,
the mesh Fleet/Tracing backends, `Benzene.Aws.Lambda.XRay`, `Benzene.Clients.Azure.ServiceBus`, the
orphaned TestHelpers packages) plus three example apps (`Cqrs`, `Cloudflare`, `K8sTransports`) that
had near-zero prior mentions. `Benzene.Mesh.Dispatch` in particular was worth prioritizing: it is the
opt-in, production-gated **live message dispatch** feature, and had never been reviewed despite being
the highest-blast-radius surface in the whole framework.

---

## §1 Mesh.Dispatch + Mesh.GoogleCloud.Storage — #185–#187

`Benzene.Mesh.GoogleCloud.Storage` came back clean — it matches its S3/Blob siblings' contracts
exactly and shares none of round 11's identified defect classes (atomic single-call writes, correct
re-throw-on-non-404). `Benzene.Mesh.Dispatch` — the live-fire production dispatch feature — did not:

**Worth-fixing:**
- **#185** — `MeshDispatchMessageHandler` hardcodes `CancellationToken.None` into the dispatch call
  instead of resolving one via `ICancellationTokenAccessor` (the framework's own established idiom
  for exactly this, used by `HttpBenzeneMessageClient`). Verified: wrapping `UseMeshDispatch()` in
  `UseTimeout(...)` gives **zero** protection — the real side-effecting dispatch runs to completion
  regardless of the configured timeout, since nothing ever supplies a cancellable token.
- **#186** — a thrown dispatch exception (target unreachable, DNS failure, malformed URL) leaves
  **zero audit trail**, unlike every other exit path in the handler, which calls `Audit(...)` first.
  Verified via mock: a failing dispatcher produces zero logger invocations. This is exactly the
  moment — a real attempt against a real production target going wrong — the package's own safety
  justification ("a scoped, attributable call that leaves a record") most needs to hold.

**Minor:**
- **#187** — `MeshDispatchRateLimiter`'s per-target windows for arbitrary/unregistered service names
  are charged before the registry validates the service exists, and are never pruned from within
  `Benzene.Mesh.Dispatch` itself (only from a sibling package's middleware). Verified: 500 distinct
  nonexistent service names each pinned a permanent dictionary entry. Only leaks in a
  shared-singleton-without-the-guard-middleware configuration — real but non-default.

**Just-noting:** `HttpMeshServiceDispatcher.DispatchAsync` buffers the entire target response with no
size cap (unlike the request-side `MaxRequestBytes` guard); no explicit dispatch timeout beyond
`HttpClient`'s default 100s, compounding #185.

---

## §2 Mesh Fleet/Tracing backends — #188–#190

`Benzene.Mesh.Fleet.Aws.XRay` came back clean (genuinely solid per-batch fetch isolation, the correct
pattern the findings below are missing), and the already-fixed round-9/10 defects (#74–#79) were
re-verified intact everywhere. `Benzene.Mesh.Fleet.Tempo` did not:

**Worth-fixing:**
- **#188** — `TempoTraceSource.GetCorrelationAsync` fetches up to 100 matched traces **fully
  sequentially** with zero per-trace fetch isolation and no concurrency at all — the same failure
  shape #75/#79 already fixed elsewhere, but worse (Jaeger at least fans out concurrently).
  `CompositeMeshFleetReadModel` wraps the whole call in one try/catch, so one trace's transient HTTP
  failure mid-loop discards the **entire correlation search**, including every trace already
  successfully fetched before the failure. Verified via two executed probes: a mid-loop failure lost
  2 already-fetchable traces out of 6, and a latency probe measured max concurrency = 1 (strictly
  serial) with no `SearchConcurrency`-style knob unlike Jaeger's.

**Minor:**
- **#189** — Jaeger's own concurrency helper, `BoundedFanOut`, caps concurrency but still has no
  *per-item* isolation — a faulted per-service task still discards other services' completed results
  via `Task.WhenAll` semantics. Same underlying gap as #188, lower severity since Jaeger's per-service
  calls share one query-service host (a shared-endpoint outage, not an isolated blip).
- **#190** — Tempo's correlation search limit is hardcoded to 100 with no override and no warning when
  hit, unlike Jaeger's `SearchLimitPerService` or X-Ray's #77-fixed logged-warning pattern.

---

## §3 Aws.Lambda.XRay + Clients.Azure.ServiceBus + orphaned TestHelpers — #191–#192

`Benzene.Aws.Lambda.XRay` and `Benzene.Clients.Azure.ServiceBus` both came back clean — every probed
scenario (annotation correctness, unresolved-transport handling, forced X-Ray segment-stack teardown,
outbound Service Bus success/failure/converter paths) behaved exactly as documented.
`Benzene.Aws.Lambda.Kinesis.TestHelpers` is faithful to the real `KinesisEvent` shape.
`Benzene.Aws.Lambda.S3.TestHelpers` is not:

**Worth-fixing:**
- **#191** — `AsS3`'s fake object key is never URL-encoded, so the real `S3ObjectKeyCodec.Decode` step
  (added in round 11's #158 fix) silently **corrupts** any test-constructed key containing `+`, `%`,
  or other S3-reserved characters. Verified: `"invoice+2024-08-27.pdf"` through the real getters comes
  back as `"invoice 2024-08-27.pdf"` — the `+` silently became a space. The existing test suite only
  ever exercises reserved-character-free keys, so this is invisible today but corrupts any consumer's
  test that uses a realistic key (dates, versioned exports). This is precisely the "TestHelpers
  produces subtly wrong fake data" risk class this area was reviewed for.

**Minor:**
- **#192** — `ServiceBusBenzeneMessageClient`'s failure-handling catch block itself throws if
  constructed with a null logger (its own `LogError` call null-guards). Unreachable through normal DI
  (which throws immediately on an unregistered service rather than returning null) and shared by every
  other `*BenzeneMessageClient` in the codebase — not unique to this package, low priority.

---

## §4 Fresh example apps — #193–#196

`Cqrs` was built and actually run end-to-end (all 5 documented scenarios executed and matched the
README exactly); `K8sTransports`'s App/Domain projects were built and run with real HTTP requests
confirming independent transport wiring; `Cloudflare`'s .NET side built and ran correctly. No
hardcoded secrets found anywhere in the three trees.

**Worth-fixing:**
- **#193** — `examples/Cqrs` and `examples/K8sTransports` are **not members of `Benzene.Examples.sln`
  at all** (zero grep matches; `Cloudflare` is listed). This is almost certainly *why* these two
  examples had near-zero prior review hits across 11 rounds: the documented build gate
  (`AGENTS.md`/`examples/CLAUDE.md`: "if you edit an example, build `Benzene.Examples.sln`") silently
  skips them, so a compile break here goes undetected by the standard verification step. Both build
  fine standalone — this is a solution-membership gap, not a compile bug (yet).
- **#194** — the Cloudflare worker's pinned `@cloudflare/containers@^0.0.15` dependency is **broken**,
  verified by actually running `npm install` + `npx wrangler deploy --dry-run`: the `0.0.15` tarball
  ships no `dist/` directory at all (the maintainer's own npm deprecation notice recommends `0.0.16+`)
  while its `package.json` `exports` point at `./dist/index.js`. The dry-run fails at the local
  bundling step — before any network/account interaction — with "Could not resolve
  `@cloudflare/containers`".
- **#195** — the same `wrangler deploy --dry-run` run flags `worker/wrangler.toml`'s
  `[containers.configuration]`/`instance_type` block as a deprecated config shape current wrangler no
  longer expects. It also diverges from the project's own `docs/getting-started-cloudflare.md`, whose
  worked example `wrangler.toml` has no such block — the example and its own how-to guide have drifted
  apart.

**Minor:**
- **#196** — `examples/K8sTransports/Domain/PlaceOrderMessageHandler.cs:23-25`'s doc comment points a
  reader at `App/HttpStartup.cs`/`App/WorkerStartup.cs` for "how one process hosts all three" —
  neither file exists (`App/` only has `Startup.cs`, which is where that explanation actually lives).
  A copying adopter would go looking for files that were never written.

**Just-noting:** dead/unused `CreateTenantRequest`/`CreateUserRequest` command types in
`examples/Cqrs` (never dispatched anywhere, harmless but confusing in a CQRS example).

---

## §5 Next steps

Per the established review→fix cadence, this document is the review record for #185–#197. No fix
packages have been designed and no code was changed by any review agent (each confirmed `git status`
clean before finishing). If a fix round is wanted, the natural groupings are: §1 (Mesh.Dispatch, one
file/blast-radius), §2 (Tempo's correlation fetch — small, self-contained), §3 (the S3 TestHelpers
key-encoding fix — trivial, plus the low-priority ServiceBus null-logger guard), and §4 (the
Cloudflare worker dependency/config fixes plus the two solution-membership additions — all small,
independent changes).
