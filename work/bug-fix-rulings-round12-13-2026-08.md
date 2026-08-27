# Rounds 12–13 fix designs — RULING + implementation plan

**Status:** ✅ **READY FOR IMPLEMENTATION** — design ruling, 2026-08-27. Covers task board
**#185–#196** (round 12) and **#198–#202** (round 13); the round-summary tasks **#197**/**#203**
close when their round's findings doc is stamped actioned. The findings themselves live in
[`bug-fix-designs-round12-2026-08.md`](bug-fix-designs-round12-2026-08.md) and
[`bug-fix-designs-round13-2026-08.md`](bug-fix-designs-round13-2026-08.md) — this document does not
restate the evidence, only the decisions. Every finding site was re-verified against `main` at
`e33c810` before ruling; the file/line references below are from that commit.

**This is a ruling document**, successor to `work/archive/bug-fix-designs-round7-10-2026-08.md` and
the round-11 rulings. Each item records a *decision*, its *rationale*, and the *rejected
alternatives*. An implementing agent must not re-litigate these or "fix" them back the other way —
if a design here doesn't survive contact with the code, amend **this document's section in the same
commit** as the divergent implementation, stating why. Same anti-flip-flop discipline as
`work/benzene-result-errors-ruling.md` and `work/settlement-consistency-fix-plan.md`.

**Four rulings worth a maintainer glance before work starts** (each flagged again in place):
WP-1's decision to *rethrow* after auditing a failed dispatch; WP-5's removal of the #133 one-
limiter-per-pipeline guard in favour of direct capture; WP-5's null-only cache-miss contract; and
WP-4's solution edits, which `AGENTS.md` says need explicit approval — granted here, recorded below.

---

## 0. Scope rulings

- **Spec:** none of these findings touch `docs/specification/**`. Mesh.Dispatch is a .NET
  backend/deployable surface (`mesh.md` explicitly excludes `benzene:mesh:query:*`); trace-source
  backends, TestHelpers, examples, cache and rate limiting are all port-level. No fixture changes,
  no re-vendoring.
- **Settlement:** nothing here overlaps `work/settlement-consistency-fix-plan.md`'s batches. That
  plan is a separately-ruled batch with its own policy table — do not fold its edits into these
  work packages, and do not touch escalation-guard polarity from here.
- **`AGENTS.md` constraints that bite this batch:** WP-4 modifies `Benzene.Examples.sln`
  (explicitly approved below, #193); no new NuGet dependencies are needed anywhere (the Cloudflare
  bump in #194 is an npm dependency of an example's worker, outside that rule's scope but recorded
  anyway); no public API signature changes except the additive options named in WP-1/WP-2.

## 1. Principles applied (carried forward, not new)

Round 7–10's P8 ("a fix lands on every sibling, not just the instance that surfaced it") drives
WP-2's Jaeger/Tempo pairing and WP-3's client-family sweep. P9 ("untrusted input bounded at the
boundary") drives WP-1's response cap and WP-5's #198 guard. P10 ("fail loudly") drives #198
throwing rather than no-op. Round 13's own lesson — two of its three worth-fixing findings are
second-order residue of round-11 fixes — is why every WP below names the prior fix it extends and
asserts the *family*, not the instance, in tests.

---

## 2. Work packages

### WP-1 — Mesh.Dispatch hardening (#185, #186, #187, + two noted gaps)

`src/Benzene.Mesh.Dispatch` — the live-fire production dispatch feature; highest blast radius in
the framework, so this WP lands first and gets the most careful review.

- **#185 (cancellation):** `MeshDispatchMessageHandler` line 118 passes `CancellationToken.None`
  into `DispatchAsync`. **Decision: resolve `ICancellationTokenAccessor` and pass its token**,
  exactly the `HttpBenzeneMessageClient` idiom (`src/Benzene.Clients.Http`), falling back to
  `CancellationToken.None` only when no accessor is registered. Test: a dispatch wrapped in
  `UseTimeout(...)` observes cancellation (mock dispatcher asserts its token fires). *Rejected:* a
  dedicated `DispatchTimeout` option — it would duplicate `UseTimeout` and `HttpClient.Timeout`;
  the missing piece is token flow, not another knob.
- **#186 (no audit on dispatch throw):** **Decision: wrap the dispatch call in try/catch; on
  exception, `Audit("dispatch-failed", …)` including the exception type, then RETHROW.** The audit
  record is the fix; the propagation semantics are not to change — a failed live dispatch must
  still surface to the caller as a failure, and swallowing it would contradict the settlement
  discipline. ⚠ *Maintainer-visible call:* audit-then-rethrow, not audit-and-return-failure-result;
  if the maintainer prefers a returned failure envelope, amend here first. *Rejected:* an
  additional pre-dispatch "dispatching" audit record — doubles the audit volume for a crash window
  the post-hoc record already narrows; revisit only if a real incident shows the gap matters.
- **#187 (rate-limiter charge-before-validate + no self-pruning):** two small decisions.
  (a) **Validate before charging:** move the `TryAcquire` call after the registry not-found check
  in `MeshDispatchMessageHandler` (currently line 90 charges before the line 101 lookup), so
  arbitrary nonexistent service names cannot pin windows. Audit-order consequence ("not-found"
  now fires without a rate-limit charge) is intended.
  (b) **Self-prune:** `MeshDispatchRateLimiter.Prune()` exists (line 83) but nothing in this
  package calls it — only the sibling `Benzene.Mesh.Artifacts` guard middleware does. Call
  `Prune()` opportunistically from `TryAcquire` when `_windows.Count` exceeds a small threshold
  (e.g. 512), so the limiter is leak-safe in the shared-singleton-without-guard-middleware
  configuration the finding demonstrated. *Rejected:* a timer — the package has no hosted-service
  machinery and shouldn't grow one for a bounded map.
- **Noted gap (response buffering), promoted into this WP:** `HttpMeshServiceDispatcher` line 48
  buffers the whole target response with no cap, asymmetric with the request-side
  `MaxRequestBytes`. **Decision: add `MaxResponseBytes` (default = the existing request cap's
  default) enforced while reading**, truncating with an audit-visible marker rather than throwing.
  P9. The second noted gap (no explicit dispatch timeout) is *covered by* #185's ruling above.

**Definition of done adds:** `Benzene.Mesh.Dispatch/CLAUDE.md` updated for all four behaviours;
the package's safety-justification paragraph (the "leaves a record" claim) now provably true under
a failing dispatcher test.

### WP-2 — Fleet trace-source fetch isolation (#188, #189, #190)

The correct pattern already ships in this codebase — `Benzene.Mesh.Fleet.Aws.XRay`'s per-batch
isolation (round 9/10's #74–#79 fixes). This WP is P8: extend it to the two siblings that missed it.

- **#188 (Tempo serial + all-or-nothing):** rework `TempoTraceSource.GetCorrelationAsync`
  (line 67's sequential `foreach`): fan out per-trace fetches via `BoundedFanOut.WhenAllAsync`
  (`src/Benzene.Core.Middleware/BoundedFanOut.cs` — already public, already referenced by Jaeger)
  with a new `TempoTraceSourceOptions.SearchConcurrency` (default 8, matching Jaeger), and wrap
  **each per-trace fetch** in its own try/catch → log warning, drop that trace, keep the rest.
  Test: N traces with one mid-set failure returns N−1 events, not zero.
- **#189 (Jaeger per-item isolation):** same isolation applied inside the existing
  `BoundedFanOut` lambda (`JaegerTraceSource.cs:111`): per-service try/catch → empty result +
  warning. **Decision: isolate in the caller's lambda, not inside `BoundedFanOut` itself** —
  the helper's `Task.WhenAll` semantics are correct for callers that *want* fail-fast; isolation
  is a per-call-site policy. *Rejected:* an `isolate: true` flag on the helper — two behaviours in
  one utility invites exactly the misuse this round found.
- **#190 (hardcoded 100):** add `TempoTraceSourceOptions.CorrelationSearchLimit` (default 100,
  keeping today's behaviour) and log a warning when the search returns exactly the limit —
  X-Ray's #77 pattern verbatim.

### WP-3 — TestHelpers fidelity + client-family guard (#191, #192)

- **#191 (S3 fake keys skip URL-encoding):** the real S3 notification pipeline URL-encodes object
  keys, and round 11's #158 fix made `S3ObjectKeyCodec.Decode` (the only member — line 25) run on
  every read, so `AsS3`'s raw key is now *wrong by construction* for any reserved character.
  **Decision: add `S3ObjectKeyCodec.Encode` (the exact inverse S3 applies: URL-encode with space
  → `+`), and have `AsS3` call it** — one codec owns both directions, so the helper can never
  drift from the getter again. Test: `"invoice+2024-08-27.pdf"` (and a `%`/unicode case)
  round-trips byte-exact through the real getter; assert `Encode`/`Decode` are inverses
  property-style over the reserved set. *Rejected:* encoding inline in the TestHelper — leaves the
  two halves free to drift, which is this bug.
- **#192 (null-logger throw in the failure path):** the hazard is family-wide — nine
  `*BenzeneMessageClient` classes share the shape (`ServiceBusBenzeneMessageClient.cs:90` et al).
  **Amendment (2026-08-27, at WP-3's implementation — count corrected, scope note added):** the
  scoping grep in this section originally said "ten"; it in fact matches ten *files* under
  `src/Benzene.Clients.*/`, but one (`Benzene.Clients.Aws.Lambda/BenzeneMessageClientRequest.cs`)
  is a data-envelope class the regex incidentally matches, not a message client — so **nine** real
  classes were in scope and fixed. Separately, an unscoped repo-wide grep turns up three more
  classes sharing the same shape *outside* `src/Benzene.Clients.*/` — `Benzene.RabbitMq/
  RabbitMqSendMessage/RabbitMqBenzeneMessageClient.cs`, `Benzene.Kafka.Core/Kafka/
  KafkaBenzeneMessageClient.cs`, `Benzene.Grpc.Client/GrpcBenzeneMessageClient.cs` — not verified
  to share the hazard (only the shape), and deliberately left **out of WP-3's scope**: a follow-up
  finding, not silently folded into this WP after the fact. **Decision: P8 sweep, one mechanical
  change across the nine in-scope classes:** constructor stores
  `logger ?? NullLogger<T>.Instance` (Microsoft.Extensions.Logging.Abstractions — already a
  transitive dependency; verify, don't add). No signature change, no behaviour change under DI.
  *Rejected:* per-call `_logger?.` — silences the symptom at one call site and leaves the next
  `LogError` added to any of the ten to reintroduce it.

### WP-4 — Examples truth (#193, #194, #195, #196, + noted dead types)

- **#193 (solution membership):** add `examples/Cqrs/**` and `examples/K8sTransports/**` projects
  to `Benzene.Examples.sln`. **This ruling is the explicit approval `AGENTS.md` requires for
  solution edits.** The build gate silently skipping two examples for 11 rounds is precisely how
  a compile break would ship; membership is the fix, not doc caveats.
- **#194 (broken Cloudflare dependency):** bump `examples/Cloudflare/worker/package.json`'s
  `@cloudflare/containers` from `^0.0.15` (ships no `dist/`, per its own deprecation notice) to
  the current release (`^0.0.16` at finding time — take latest verified). Done = `npm install` +
  `npx wrangler deploy --dry-run` completing the local bundling step cleanly, the same probe the
  finding ran.
- **#195 (wrangler config drift):** update `worker/wrangler.toml` to the current schema (the
  deprecated `[containers.configuration]`/`instance_type` block, lines 17–18) and align it with
  `docs/getting-started-cloudflare.md`'s worked example — **the guide is the reference; the
  example conforms to it**, not the other way round. If the current wrangler schema genuinely
  needs a shape the guide lacks, update both together in this WP.
- **#196 (phantom files in doc comment):** `examples/K8sTransports/Domain/PlaceOrderMessageHandler.cs`
  doc comment → point at `App/Startup.cs` (the files it names were never written).
- **Noted dead types, included:** delete `CreateTenantRequest`/`CreateUserRequest` from
  `examples/Cqrs` — never dispatched, and confusing precisely because it's a CQRS example.

### WP-5 — Cache + RateLimiting second-order fixes (#198, #199, #200, #201, #202)

Round 13's findings are residue of round 11's own fixes; each ruling below names the fix it
extends and must not weaken it.

- **#198 (empty prefix wipes the database):** `RedisCacheService.CreatePrefixActions` (line 109)
  turns an empty/whitespace prefix into pattern `"*"`. **Decision: throw `ArgumentException` on
  null/empty/whitespace prefix, message naming `CreateWildcardActions("*")` as the deliberate
  route to invalidate-everything.** P10: one bad tenant-id interpolation away from a cache wipe
  is exactly where failing loudly beats convenience. *Rejected:* silent no-op (hides the caller's
  bug and under-invalidates — the mirror-image data bug).
- **#199 (mapping delegates outside #139's protection):** in the 3-arg
  `CacheWriteActions.WriteThroughAsync` (line 61), `getCacheAction(result)` and
  `getCacheValue(result)` run after the DB commit but outside the try/catch
  `SyncCacheAfterWriteAsync` provides. **Decision: evaluate each delegate in its own try/catch;
  on throw, `LogError` ("cache mapping delegate failed after the database write; result returned,
  cache not updated") and fall through to the no-op branch, returning the result.** This is
  #139's own contract ("a cache-side failure must not surface as the operation's failure")
  extended to the delegates that feed the cache side. Cancellation semantics unchanged (#141's
  open decision is not touched). Test: throwing `getCacheAction` after a successful write returns
  the result and logs; same for `getCacheValue`.
- **#200 (the #133 guard breaks multi-transport pipelines):** the one-internally-owned-limiter
  guard (`Extensions.cs:256–279`) keys on the shared `IBenzeneServiceContainer`, which sibling
  pipelines share by design. **Decision: delete the container registration and the guard;
  capture the created limiter directly in the middleware closure** — precisely how the BYO
  `UseRateLimiting(RateLimiter, …)` overload two screens up already works (line 44–52,
  `ownsLimiter: false`; internal path keeps `ownsLimiter: true` so disposal ownership — the
  actual point of #133 — is preserved by the middleware, not the container). The collision the
  guard defended against (two limiters shadowing one DI key) is impossible once nothing is
  registered under that key. Stacked `Use*RateLimiting` calls in one pipeline become legal and
  independent; update the exception-message-turned-doc accordingly.
  ⚠ *Maintainer-visible call + implementer validation:* re-read round 11's #133 finding before
  landing, and add a disposal test (pipeline disposal disposes an internally-created limiter,
  BYO limiter untouched) proving the leak fix survives the guard's removal. *Rejected:* keying
  the guard per-pipeline via builder-identity tracking (`ConditionalWeakTable`) — machinery whose
  only job is preserving a restriction that direct capture makes unnecessary.
- **#201 (custom serializer breaks negative caching):** presence detection
  (`CacheEntry.TryReadEntryAsync`, line 47: `!string.IsNullOrEmpty`) conflates "no entry" with
  "cached empty string". **Decision: `null` is the only miss marker — presence becomes
  `cacheValue is not null`.** Prerequisite in the same WP: audit every `ICacheService`
  implementation (Redis, in-memory, any others) and assert-by-test that a genuine miss yields
  `null`, never `""`, and that a stored `""` round-trips as present. Document on the `ISerializer`
  seam (#145's addition) that empty-string output is a valid cached representation. This
  re-closes #140's cache-penetration scenario for the serializer class the docs invite.
- **#202 (misattributed ObjectDisposedException):** split the single catch
  (`RateLimitingMiddleware.cs:92`) into two — one around the cost delegate, one around
  `Acquire()` — with source-accurate messages ("a dependency used by the permit-cost delegate was
  disposed" vs "the rate limiter has already been disposed"). **Both still fail CLOSED** — #143's
  fail-closed ruling is not reopened; only the diagnostic precision changes.

---

## 3. Implementation plan

**Preconditions.** Base: `origin/main` at `e33c810` or later; rebase per `AGENTS.md`. Reconfirm the
round-11 closing baseline before starting and after the last merge: `dotnet build Benzene.sln -c
Release` 0 errors; `test/Benzene.Core.Test` ≥3178 passed / 0 failed; `test/Benzene.Mesh.Test`
556/556; `deploy/Mesh/Benzene.Mesh.Host.Test` 150/150; `Benzene.Examples.sln` build 0 errors —
plus, from WP-4 onward, the two newly-added example projects building inside it.

**Sequencing.**
1. **WP-5 first** — it unblocks round 13's stated next step (a second blind re-audit of the same
   two packages measuring 5 → fewer), and its #198 is the most dangerous open finding in either
   round.
2. **WP-1 second** — production-dispatch safety; small surface, biggest consequence.
3. **WP-2, WP-3, WP-4 in parallel worktrees**, one agent each, disjoint trees. Same host-contention
   caveat as every prior round: 2–3 concurrent builds, no more. Merge order unconstrained;
   resolve the mechanical `outstanding-bugs.md` append-conflicts by keeping both sections.

**Per-package definition of done** (unchanged from the round-7-10 ruling): revert-verified
red→green test per code fix; XML docs + the named `docs/*.md` pages + `docs/capability-matrix.md`
rows updated in the same package; `[RESOLVED]` line per finding in `outstanding-bugs.md` under a
"Tracked findings rounds 12–13" section pointing here; task board entries → completed; one logical
change per commit; push with retry/backoff per repo convention.

**Round completion:** all baselines green (examples now including Cqrs/K8sTransports); the two
findings docs get their status flipped to actioned; docs-archivist moves them and this ruling to
`work/archive/` stamped with landing commits; #197/#203 closed. If the round-13 follow-up
blind re-audit is run, it targets the post-WP-5 commit and its findings open a new round doc —
they do not reopen this one.

**Amendment rule (repeat):** a design here that doesn't survive contact with the code is amended
in this document in the same commit as the divergent implementation — the record and the code
never disagree.

---

## 4. Task-number index

| Task | WP | Ruling in one line |
|---|---|---|
| #185 | WP-1 | Dispatch resolves `ICancellationTokenAccessor`; no new timeout knob |
| #186 | WP-1 | Audit `dispatch-failed` then rethrow |
| #187 | WP-1 | Validate target before charging; limiter self-prunes past a size threshold |
| #188 | WP-2 | Tempo correlation: bounded fan-out + per-trace isolation + `SearchConcurrency` |
| #189 | WP-2 | Jaeger: per-service isolation in the call-site lambda, not in `BoundedFanOut` |
| #190 | WP-2 | `CorrelationSearchLimit` option + at-limit warning (X-Ray #77 pattern) |
| #191 | WP-3 | Add `S3ObjectKeyCodec.Encode`; `AsS3` uses it; inverse-pair test |
| #192 | WP-3 | `NullLogger` fallback in the nine in-scope `*BenzeneMessageClient` constructors (P8); 3 siblings outside `src/Benzene.Clients.*/` left for a follow-up finding |
| #193 | WP-4 | Cqrs + K8sTransports join `Benzene.Examples.sln` (solution edit approved here) |
| #194 | WP-4 | `@cloudflare/containers` → current release; dry-run is the acceptance test |
| #195 | WP-4 | `wrangler.toml` modernised to match the getting-started guide |
| #196 | WP-4 | Doc comment points at the real `App/Startup.cs` |
| #197 | — | Round-12 summary; closes at round completion |
| #198 | WP-5 | Empty prefix throws; `CreateWildcardActions("*")` is the deliberate route |
| #199 | WP-5 | Mapping delegates get #139's protection; log + return result on throw |
| #200 | WP-5 | Guard deleted; limiter captured directly (BYO-overload pattern); disposal test |
| #201 | WP-5 | `null` is the only cache-miss marker; store audit + serializer-seam doc |
| #202 | WP-5 | Two catch blocks, source-accurate messages; still fail closed |
| #203 | — | Round-13 summary; closes at round completion |
