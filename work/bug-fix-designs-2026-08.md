# Tracked-findings fix designs (rounds 5–6) — RULING + implementation plan

**Status:** ✅ **APPROVED for implementation** — design ruling, 2026-08-25. Covers all 27 findings on
the shared task board (tasks #1–#27), produced by the round-5/round-6 adversarial review passes.
Every finding here was **verified with concrete evidence** (live reproduction, stress test, or
compiler-verified probe) before being tracked; none is speculative.

**This is a ruling document.** Each item below records a *decision*, its *rationale*, and the
*rejected alternatives*. A future agent implementing, reviewing, or re-reviewing this code must not
re-litigate these decisions or "fix" them back the other way — if a decision here proves wrong in
practice, amend **this document first** (or its successor in `work/archive/`), stating why, then
change the code. This is the same anti-flip-flop discipline as
`work/benzene-result-errors-ruling.md`.

**Lifecycle:** working doc. When all work packages land, the docs-archivist moves this file to
`work/archive/` (stamped), `work/outstanding-bugs.md` gains the resolved entries, and
`docs/capability-matrix.md` is updated per package. Until then this file is the source of truth for
the fix round.

**Task board mapping:** every section is headed by its task number(s). The board (shared TaskList,
tasks #1–#27) is the authoritative open/closed state; this doc is the authoritative *design*.

---

## 0. Spec scoping ruling — none of these touch `docs/specification/**`

The language-neutral spec (main Benzene repo, `docs/specification/**`) covers wire contracts, the
status vocabulary, mesh **wire** contracts, contract documents/hashes, and the Cloud Service
Profile. Checked against each finding:

- **Mesh host auth/config (#3, #4, #5, #6, #19, #20, #27)** — `deploy/Mesh/Benzene.Mesh.Host` is a
  deployable product of this repo, not a spec surface. Its config semantics are documented in the
  host's `CONFIG.md`/`README.md` and `work/enterprise/slice-2-auth.md`. No spec change.
- **Mesh query robustness (#22)** — `mesh.md` line 389 *explicitly excludes* `benzene:mesh:query:*`
  from the cross-language contract ("deliberately not part of this contract"). .NET-side fix only.
- **Descriptor `required` ordering (#7)** — the spec pins hashes of *given documents*
  (`contract-hash-cases.json`); it does not pin a port's reflection-driven generation order.
  Determinism of generation is a port-quality concern, fixed here.
- **Store fencing (#16, #17)** — `IIdempotencyStore`/`IOutboxStore` are .NET port APIs; the spec
  does not define store ports. The durable contract lives in the interfaces' XML docs and
  `docs/capability-matrix.md`.
- **gRPC null response (#8, #23)** — the spec's `conformance/grpc-status-mapping.json` maps
  `accepted → OK`, so a conforming port MUST be able to return a success for a handler that
  produced no payload. Today the .NET port *cannot* (it throws). The fix **aligns the port with the
  existing spec**; the spec itself needs no change.
- **Schema compatibility comparers (#25)** — comparer semantics are .NET tooling, not spec-pinned
  (the spec's `contract-document.md` closure walk covers `oneOf`/`anyOf`/`allOf` for *client
  generation and hashing*, not for compatibility diffing). **Forward-looking note:** if a second
  language port ever ships a compatibility comparer, the "which changes are breaking, in which
  direction" table below is the candidate content for a new spec section + conformance fixture. Do
  not add it to the spec now — the spec stays taut (it covers what a conforming *service* must do).
- Everything else (#1, #2, #9–#15, #18, #21, #24, #26) is implementation detail of this port.

**Ruling: zero changes to `docs/specification/**` in this fix round.** Anyone who believes a fix
here needs a spec change must raise it as a deliberate spec change per `AGENTS.md` (fixtures
updated, all ports re-verify) — not slip it in.

---

## 1. Cross-cutting principles adopted (the durable rules)

These generalize the individual fixes into rules future work is held to. Each gets baked into the
living docs named in the work packages.

- **P1 — Config is validated for satisfiability, not just syntax.** A configuration that *reads* as
  enabling a behavior but *structurally cannot* deliver it is rejected at startup with a message
  naming the offending keys. (Established by the `dispatchRole`+`mode:none` fix; generalized by
  WP-1's mode×option matrix.)
- **P2 — Every claim/lease API is fenced.** An API that hands out a claim returns an opaque token;
  every settle/complete/release call presents it; a mismatched token is a refused write, reported
  to the caller (return `false`), never a silent clobber. Fencing narrows, but does not eliminate,
  at-least-once windows (crash-after-send remains inherent) — docs must say so honestly. (WP-3.)
- **P3 — "No response" is a legitimate handler outcome on every transport.** A fire-and-forget
  handler producing a success status with no payload must map to that transport's natural empty
  success (HTTP: status + empty body; gRPC: mapped status + empty message; queue transports: ack).
  Already fixed for ASP.NET Core (`8ea5bdb`) and verified structurally safe for API Gateway and
  Azure Functions AspNet; WP-4 closes the two remaining gRPC gaps, which ends this bug class
  codebase-wide.
- **P4 — I/O on a request path accepts ambient cancellation.** Middleware and health checks thread
  the ambient `CancellationToken` into every store/SDK call so `UseTimeout` and processor timeouts
  actually bound work instead of abandoning it. (WP-7.)
- **P5 — Query-side inputs get the same "never throw, degrade to absent" discipline as ingest.**
  The collector's ingest side already has the "no feed fails ingestion" rule; the query side's
  parsers get the same: unparseable or unrepresentable → treated as absent, never an exception.
  (WP-2.)
- **P6 — No inert options.** An option or flag whose documented behavior is not actually delivered
  is a bug worse than the option's absence: it must be implemented or removed in the same change
  that discovers it. Nothing is kept "for backward compatibility" — standing pre-1.0 directive.
  (WP-8 is the instance; the rule is general.)
- **P7 — Examples state their security posture.** Every example that deploys something publicly
  reachable states its auth/exposure posture in its README, even (especially) when that posture is
  "none, demo only". Parity target: the AwsMesh (full guards) / K8sMesh (explicit disclaimer)
  spectrum — never silence. (WP-2.)

---

## 2. Work packages

Each package is sized for one agent in one isolated worktree. **Per-fix discipline (all packages):**
red→green regression test (write the test, revert the fix, watch it fail, restore, watch it pass);
XML/contract docs updated in the same commit as the behavior; one logical change per commit.

### WP-1 — Mesh host: auth satisfiability matrix, OIDC hardening, logout, dispatch wiring
**Tasks #3, #6, #19, #27, #20, #4, #5.** Files: `deploy/Mesh/Benzene.Mesh.Host/{MeshAuthGate.cs,
Startup.cs, CONFIG.md, README.md}`, `src/Benzene.Mesh.Artifacts/` (UI wiring), host tests.

> **Implementation note (2026-08-25):** the "UI wiring" pointer above is corrected here rather than
> left to mislead a future reader — `UseMeshUi`/`MeshUiMiddleware`/`MeshUiPage` (the `dispatchUrl`/
> `logoutUrl` parameters (c)/(d) rely on) live in `src/Benzene.Mesh.Ui/`, not
> `src/Benzene.Mesh.Artifacts/` (that package holds `MeshRefreshGuardMiddleware`/
> `MeshDispatchGuardMiddleware` - the CSRF-header convention (c) reuses - plus `UseMeshArtifacts()`,
> but not the UI page itself). Both `UseMeshUi`'s `dispatchUrl`/`logoutUrl` parameters and the C#-side
> plumbing to render them (`data-dispatch-url`/`data-logout-url` on the page root) had already landed
> in `Benzene.Mesh.Ui` before this work package, covered by `MeshUiPageTest`; what was actually
> missing and is fixed here is (1) `Startup.cs` never calling `UseMeshUi` with either parameter, and
> (2) the mesh UI's Sign-out control being a plain `<a href>` GET link (`src/Benzene.Mesh.Ui/mesh-ui.html`),
> which a GET-rejecting logout endpoint would have made silently non-functional - it now POSTs with
> the CSRF header and navigates on the JSON response, per (c)'s spec below.

**(a) The mode×option satisfiability matrix (#3, #6, #19, #27).** Extend `MeshAuthGate.Validate()`
— which already rejects `dispatchRole`+`mode:"none"` — into a complete matrix. `Validate()` MUST
reject, at startup, with a message naming the keys:

| Option set | `none` | `basic` | `proxy` (no groupsHeader) | `proxy` (+groupsHeader) | `oidc` |
|---|---|---|---|---|---|
| `RequiredGroups` | ✗ reject | ✗ reject | ✗ reject | ✓ | ✓ |
| `dispatchRole` | ✗ reject (exists) | ✗ reject (#27) | ✗ reject (#6) | ✓ | ✓ |
| `AllowedEmailDomains` | ✗ reject (#3) | ✗ reject | ✓ | ✓ | ✓ |
| `dispatch.enabled` | ✗ reject (#19) | ✓ | ✓ | ✓ | ✓ |

Rationale rows: group/role options require a mode that can carry group claims (`oidc`, or `proxy`
with `groupsHeader`). `AllowedEmailDomains` requires an email-bearing identity (`proxy`/`oidc`);
under `basic` the operator defines the one account themselves, so domain-filtering it is
meaningless — reject rather than silently ignore. `dispatch.enabled` requires *any* established
identity (the dispatch guard is fail-closed on identity, proven by #19's live repro), so `none` is
rejected; `basic` is allowed (its Name-claim identity satisfies the guard).

**Rejected alternative (recorded):** a `MESH_BASIC_ROLES` env knob to let the basic-auth account
carry roles. Rejected to keep `basic` a deliberately minimal single-account mode; anyone needing
roles has outgrown `basic` and should use `proxy`/`oidc`. Do not add this knob casually later —
amend this ruling first.

The matrix table above goes verbatim into the host's `CONFIG.md` (new "Which options work under
which auth modes" section) and `work/enterprise/slice-2-auth.md` gets a pointer. Also: add
`auth.dispatchRole` to `MeshConfigSummary.Format` (the startup summary omission noted in #27).

**(b) OIDC non-https authority (#20).** Add `auth.oidc.requireHttpsMetadata` (default `true`).
`Validate()` rejects an `http://` authority unless it is explicitly `false`; the value is passed to
the OIDC options (`RequireHttpsMetadata`). `CONFIG.md` documents it with a "local-dev only, never
production" warning. This preserves fail-fast (P1) while supporting the host's own Docker-first
local story.

**(c) OIDC logout (#4).** Map `POST /mesh/auth/logout` in the host (OIDC mode only): requires the
custom CSRF header (same custom-header CSRF convention as refresh/dispatch guards), signs out the
cookie session, responds `{"redirect": <end_session_url or null>}` — the end-session URL built from
the IdP's advertised `end_session_endpoint` (with `post_logout_redirect_uri`) when discovery
provides one, else `null` (local sign-out only). `UseMeshUi` gains a `logoutUrl` parameter; the UI
shows Sign out when set, POSTs, then navigates to `redirect` if non-null else reloads. GET-based
logout is rejected (CSRF-forced logout).

**(d) dispatchUrl wiring (#5).** Pass `dispatchUrl` to `UseMeshUi(...)` in `Startup.cs` (~line 352)
whenever `dispatch.enabled`; host test asserts the rendered UI config contains it. (With (a),
`dispatch.enabled` now implies an auth mode where dispatch can actually work.)

### WP-2 — Mesh collector robustness, deterministic schema, example posture
**Tasks #22, #7, #21.**

**(a) #22 — `MeshTimeRangeResolver.ParseDuration`** (`src/Benzene.Mesh.Collector/MeshTimeRangeResolver.cs:93-118`):
a count that would overflow `TimeSpan` is treated exactly like an unparseable bound — **absent,
never thrown** (extend `ParseBound`'s existing contract; implement as a bounds check or
`catch (OverflowException) → null`). The round-6 probe (`From = "now-100000000d"` →
`OverflowException` → unhandled 500 on `mesh:query:*`) becomes the regression test. Record P5 in
the collector's `CLAUDE.md` beside the ingest-side rule it mirrors.

**(b) #7 — `MeshSchemaGenerator`** (`src/Benzene.Mesh.Wire/MeshSchemaGenerator.cs`): emit the
`required` array sorted `StringComparer.Ordinal` — the same deterministic ordering `properties`
already uses; reflection order is unspecified and can differ across runtimes, silently changing
descriptor hashes. **Consequence, accepted:** hashes change once for services whose reflection
order wasn't already alphabetical; pre-1.0 this is acceptable and determinism is the contract.
Regression test: a type whose declaration order differs from alphabetical produces sorted
`required`.

**(c) #21 — `examples/AzureMesh`**: wire `UseMeshRefreshGuard` (CSRF + throttle on
`POST /mesh/refresh` — same package AwsMesh already uses, zero new infra) **and** add the sibling
examples' explicit README disclaimer ("the mesh host itself is publicly reachable and
unauthenticated — demo-only posture"), per P7. Full OIDC for this example is *not* in scope — the
disclaimer + refresh guard is the decided posture, matching AzureFunctionsMesh/GoogleCloudMesh/
K8sMesh.

### WP-3 — Claim fencing for Outbox and Idempotency stores
**Tasks #16, #17.** One design, two subsystems — implement together (shared reviewer context), P2.

**Idempotency (#16)** — `src/Benzene.Idempotency/IIdempotencyStore.cs` + InMemory + DynamoDb stores
+ the middleware:
- `ClaimResult` gains `string? ClaimToken` — non-null exactly when the claim was `Won` (store-minted
  opaque value, e.g. a GUID string).
- `CompleteAsync(string key, string claimToken, bool wasSuccessful, …)` and
  `ReleaseAsync(string key, string claimToken, …)` now **require** the token and return
  `Task<bool>`: `false` = there is no live claim with that token (it lapsed and was reclaimed, or
  was already settled) and **nothing was written**.
- InMemory: compare token under the existing lock. DynamoDb: token is an attribute on the record;
  settle writes use a `ConditionExpression` on token equality (`ConsistentRead` discipline already
  established for this store family applies).
- Middleware: a `false` settle result is logged as a warning ("claim was reclaimed by another
  worker; outcome recorded by the new holder") and is **not** an error — the new holder owns the
  outcome. The round-5 deterministic repro becomes the regression test.

**Outbox (#17)** — `src/Benzene.Outbox/IOutboxStore.cs`, `OutboxEnvelope`, `OutboxDispatcher`, and
all three stores (InMemory, DynamoDb `:151-176`, EF `:174-188`):
- `OutboxEnvelope` gains `string? LeaseToken`, stamped (rotated) by the store on every successful
  `ClaimDueAsync`/`ClaimAsync`.
- `MarkDispatchedAsync(string id, string leaseToken, …)`, `RescheduleAsync(…, string leaseToken, …)`,
  `ParkAsync(…, string leaseToken, …)` require the token and return `Task<bool>` with the same
  `false` = "not the current lease holder / gone, nothing written" semantics. DynamoDb: extend the
  existing `ConditionExpression` to token equality (today it only checks `attribute_exists(#pk)`);
  EF: token in the `WHERE`; InMemory: compare.
- `OutboxDispatcher.DispatchEnvelopeAsync` (`:90-134`) passes the claimed envelope's token and logs
  a warning on `false`.
- **Honest contract rewrite** in `ClaimDueAsync`'s XML remarks: fencing closes the
  live-but-slow-claimant hole (the round-6 stress test's `sendCount == 2`); the crash-after-send
  window remains inherent to at-least-once, and a send already in flight when the lease lapses can
  still deliver — fencing prevents the *state clobber* and makes the lost lease *visible*, it does
  not recall a sent message. `Benzene.Outbox/CLAUDE.md` and `docs/capability-matrix.md` updated to
  match.

**Breaking-change ruling (both):** pre-1.0, **no** compatibility overloads, no default parameters
that let a caller skip the token — a skippable fence is no fence. All call sites updated in the
same change.

### WP-4 — gRPC null-response crash (unary + client-streaming)
**Tasks #8, #23.** Files: `src/Benzene.Grpc/GrpcMethodHandler.cs` (lines ~44 and ~77),
`src/Benzene.Grpc/Serialization/ProtobufJsonGrpcMessageAdapter.cs` (`ConvertResponse`).

**Decision:** `ConvertResponse<TResponse>` no longer throws on a null payload — a null payload
converts to an **empty `TResponse` message instance** (created via the message descriptor's parser
/ `Activator`; protobuf message types always have a parameterless constructor). The
`BenzeneException("Cannot convert a null payload…")` branch and its `<exception>` doc are removed;
the doc now states: *a null payload is a fire-and-forget success and yields an empty response
message, so the mapped status (spec: `accepted → OK`) reaches the client rather than an opaque
`Unknown`.* Fixing the adapter (not the two call sites individually) closes **both** call sites at
once and is why this is one decision, not two.

Server-streaming and duplex are **not** touched — round 6 verified they already return a
controlled `RpcException(Internal, …)` for a null stream, which is correct (a streaming RPC with
no stream is a wiring error, not fire-and-forget).

Tests: fire-and-forget unary **and** client-streaming handlers driven through real
`TestServer` + `GrpcChannel` (the technique that found the bug), asserting `OK` + empty message.
This ends the P3 bug class codebase-wide — note that in `docs/getting-started-grpc.md`.

### WP-5 — Azure: source-generator diagnostics; Service Bus settle ordering
**Tasks #9, #11, #10.**

**(a) #9 + #11 — one work item: give `AzureFunctionTriggerGenerator` a diagnostics path, then use
it.** (`src/Benzene.Azure.Function.SourceGenerators/…`.) Introduce a `DiagnosticDescriptors` table:
- `BENZ0001` (Error): duplicate `[Function(name)]` literal across generated triggers — **fail the
  build**; do not emit the colliding function. Round 6 proved the collision is cross-transport
  (any two triggers named `"dup"` collide), so dedup keys on the *name literal* globally.
  **Rejected alternative (recorded):** auto-uniquifying the function name the way `ClassName`
  already is. Rejected because a Function *name* is externally meaningful (bindings, host.json,
  scale rules, portal identity) — silently renaming it moves the failure to deployment. The class
  name is an internal artifact and stays auto-uniquified; the function name must be the user's to
  fix, at build time, with a clear message.
- `BENZ0002` (Error): CosmosDb trigger missing `DocumentType`
  (`Transports/MessagingTransports.cs`'s current silent `continue`) — a declared trigger that is
  silently *not generated* is the worst outcome; report and fail instead.
- Future generator complaints join this table rather than inventing new mechanisms.
Tests via `CSharpGeneratorDriver` asserting the diagnostics (round 6's probe technique).

**(b) #10 — `ServiceBusApplication.OnPipelineSucceededAsync`**
(`src/Benzene.Azure.Function.ServiceBus/ServiceBusApplication.cs:119-138`): set
`state.Acked = true` **only after** the settle call (`CompleteMessageAsync`/`AbandonMessageAsync`)
returns successfully — today it is set before, so a throwing settle call skips the base class's
fallback-abandon (gated on `!state.Acked`) exactly when it is needed. Red→green: a settle stub that
throws must trigger the base fallback-abandon.

### WP-6 — AWS clients: Lambda invocation semantics; Step Functions idempotent starts
**Tasks #12, #13, #14.**

**(a) #12 — `UseAwsLambda<T>()`** (`src/Benzene.Clients.Aws.Lambda/{SqsContextConverter.cs,
AwsLambdaClientMiddleware.cs}` — the `LambdaContextConverter<T>` path):
- The `<T, Void>` fire-and-forget shape sets `InvocationType = Event` (async invoke) — the shape's
  own contract, currently silently `RequestResponse`.
- `MapResponseAsync` stops returning unconditional `Accepted`: for `Event` invokes, a 2xx
  `StatusCode` → `Accepted`, anything else → failure; for request-response invokes, a non-null
  `InvokeResponse.FunctionError` → failure result carrying the error (never `Accepted`).
- Document the two shapes' semantics in `docs/clients.md`.

**(b) #13 — `StepFunctionsClient`** (`src/Benzene.Clients.Aws.StepFunctions/StepFunctionsClient.cs`):
on `ExecutionAlreadyExistsException`, call `DescribeExecution` and compare the existing execution's
`input` to the requested input (exact-string compare of the serialized input; document that
callers must serialize deterministically): match → `Accepted` (true idempotent duplicate),
mismatch → **`Conflict`-status failure** (the caller's payload was NOT started). Update
`docs/aws-iam-permissions.md` (+`states:DescribeExecution`). **Rejected alternative (recorded):**
returning a distinct "already started, unverified" success — rejected; it re-creates the silent
wrong-input hazard this fix exists to close, and the extra call happens only on the rare
already-exists path.

**(c) #14 — `SanitizeExecutionName`**: whenever the sanitized name differs from the original
(character replacement *or* length truncation), the result is the sanitized name truncated to 71
chars + `-` + the first 8 lowercase hex chars of SHA-256 of the original name (UTF-8). Fully
deterministic (same input → same name, preserving idempotent-start semantics with (b));
collision-resistant across distinct originals that sanitize alike. Regression tests: a >80-char
pair differing only past the cut, and a `"a/b"` vs `"a.b"` replacement-collision pair, must yield
distinct names; an already-clean name is unchanged.

### WP-7 — Cross-cutting hygiene: cancellation, eviction, Saga per-run state
**Tasks #1, #2, #26, #18, #15.** ⚠️ Contains the one broad interface change (#2) — see sequencing.

**(a) #2 — `IHealthCheck` cancellation.** Change the core contract:
`Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)` (in
`src/Benzene.HealthChecks.Core/IHealthCheck.cs`). Every implementer (20+) updates; each forwards
the token into its SDK/network calls (DynamoDb `DescribeTable`, EF `OpenConnection`, ServiceBus
ops, etc. — the three named in #2, plus mechanical pass-through everywhere else).
`TimeOutHealthCheck`/`HealthCheckProcessor` pass a timeout-linked token so a timeout **cancels**
the underlying call instead of abandoning it. Pre-1.0: no default-parameter escape hatch on the
interface, no parallel overload — one method, token required. This is the P4 keystone.

**(b) #26 — `SqsHealthCheck`** (`src/Benzene.Clients.Aws.Sqs/SqsHealthCheck.cs:82-89`): **delete**
the internal `Task.WhenAny`+`Task.Delay` guard; rely on the processor's uniform timeout wrap like
its Sns/EventBridge siblings (which, after (a), genuinely cancels). The richer per-check timeout
message is forfeited — consistency wins; the check's dependency tags still identify it. Done inside
WP-7 because (a) rewrites the same file. Test: processor timeout bounds a hung SQS call.

**(c) #1 — claim-check middleware cancellation.** `IClaimCheckStore.Get/PutAsync` **already**
accept a `CancellationToken` — the bug is only that `ClaimCheckHydrateMiddleware`/
`ClaimCheckOffloadMiddleware` never pass the ambient one. Resolve the ambient token (the
`ICancellationTokenAccessor` mechanism the batch applications already seed) and pass it. No
interface change. Test: a hung fake store + `UseTimeout` → the pipeline is bounded.

**(d) #18 — `InMemoryClaimCheckStore` eviction** (`src/Benzene.ClaimCheck/InMemoryClaimCheckStore.cs`):
two-part reclamation, no background thread: (1) `GetAsync` removes an entry it finds expired
(under the existing lock); (2) `PutAsync` runs a time-gated sweep — at most once per minute, purge
all `ExpiresAt <= now` entries — covering payloads that are never read back (fan-out siblings,
undelivered messages). Class doc rewritten to state the actual reclamation behavior (the current
"expired lazily" wording implies release that never happens). **Rejected alternative (recorded):**
a background timer — rejected; a dev/single-worker store should not own a thread, and sweep-on-put
bounds growth wherever growth originates. Test: expired entries actually leave the dictionary.

**(e) #15 — Saga per-run state** (`src/Benzene.Saga/{Saga.cs, Stage.cs, SagaStep.cs}`): move all
per-execution outcome fields out of `SagaStep<T>`/`Stage` instances into a run-scoped state object
created inside `RunAsync()` (per-step slots keyed by step index); steps/stages become immutable
descriptors after `Build()`. The **contract** — "a built `Saga` is immutable and safe for
concurrent `RunAsync` calls" — goes into `Saga`'s XML docs and `docs/capability-matrix.md`. The
round-5 concurrent stress test (300 runs, 6 corrupted) becomes the regression test and must pass
0-corrupted.

### WP-8 — RabbitMQ `mandatory` made real
**Task #24.** (`src/Benzene.RabbitMq/RabbitMqSendMessage/RabbitMqClientMiddleware.cs:39-49`.)

**Decision: implement it (P6 — no inert options).** Design: when `mandatory: true`,
(1) wiring **requires** a publisher-confirms-enabled channel and fails fast at setup otherwise
(returns are only ordered/meaningful relative to a publish when confirms sequence them);
(2) the middleware stamps a `MessageId` if absent, subscribes one channel-level
`BasicReturnAsync` handler, and correlates returns to in-flight publishes by `MessageId`;
(3) a returned message resolves that publish as **failed** (`Published = false` → failure result)
instead of today's unconditional `Accepted`. Unit-test the correlation tracker; broker-level
behavior goes in the docker-gated integration suite alongside the existing RabbitMQ tests.

**Recorded fallback (the only sanctioned alternative):** if implementation shows RabbitMQ.Client
7.x's confirm/return interplay cannot make this reliable, the flag (and its docstring promise) is
**removed entirely** and the real feature becomes a roadmap entry in `work/outstanding-bugs.md` —
per P6, a half-working or inert reliability flag must not survive either way. What is *not*
sanctioned: leaving it as-is, or "documenting the limitation" while keeping the inert flag.

### WP-9 — Schema compatibility: union-aware walkers
**Task #25.** (`src/Benzene.Schema.OpenApi/Compatibility/SchemaCompatibilityComparer.cs:106-177`
and its deliberately-identical twin `src/Benzene.Schema.Compatibility/JsonSchemaComparer.cs:58-130`
— **both must change together**; the shared-corpus test enforces parity.)

New `SchemaChangeKind`s: `UnionVariantAdded`, `UnionVariantRemoved`, `UnionVariantChanged`
(recursed), and treat an `Items` null-asymmetry as a type change (today: silently skipped when
only one side has `Items`).

**Matching rule** for `oneOf`/`anyOf` members: by discriminator mapping value when a discriminator
is present; else by `$ref` target name; else by index. `allOf`: match `$ref` members by target
name, inline members by position; a member added/removed is reported.

**Breaking-direction table (the ruling — mirror of the comparer's existing request/response
direction logic for `Required`):**

| Change | Request schema (what the service accepts) | Response schema (what consumers receive) |
|---|---|---|
| Union variant **removed** | **Breaking** (callers still sending it are rejected) | Non-breaking |
| Union variant **added** | Non-breaking | **Breaking** (consumers meet an unknown variant) |
| `Items` appears/disappears on one side | Breaking (as type change) | Breaking (as type change) |

Docs: the change-kind vocabulary and this table go into `docs/contract-artifacts.md`. Regression:
the round-6 probe (`oneOf:[Dog,Cat]` → `oneOf:[Dog]` reported **zero** changes) must now report
`UnionVariantRemoved`, plus corpus cases for each row above, run against **both** walkers.

---

## 3. Implementation plan (for the fix-round agent(s))

**Preconditions.** Base: `origin/main` (currently `2561c0b`). Task board #1–#27 all `pending`.
Baseline to reconfirm before starting and after finishing: `Benzene.Test.dll` ~2821 passed /
2 skipped / 0 failed; `Benzene.Mesh.Test` 505; `Benzene.Mesh.Host.Test` 99; all 12 templates pass
the pack→install→new→test loop. (Known flake under heavy host contention:
`TimeoutMiddlewareTest.HandleAsync_NestedUseTimeout_…` — re-run in isolation before treating as a
regression.)

**Sequencing.**
1. **WP-7 first, alone** — it changes `IHealthCheck` across 20+ implementers and touches
   `SqsHealthCheck` (overlapping WP-6's project); landing it first keeps every other package's
   worktree conflict-free. It is mechanical and low-risk.
2. **Then WP-1 … WP-6, WP-8, WP-9 in parallel worktrees** (`git worktree add --detach`), one agent
   each; they touch disjoint projects. Cap concurrency to what the shared 4-core host tolerates
   (2–3 building at once; contention, not code, is the usual cause of stalls — see prior rounds).
3. Merge order among the parallel set is unconstrained; each merges to `main` when green.

**Per-package definition of done** (docs lifecycle is part of done, per `AGENTS.md`):
- Every fix has its red→green regression test (revert-verified).
- XML/contract docs, the named `docs/*.md` pages, and `docs/capability-matrix.md` updated in the
  same package.
- `work/outstanding-bugs.md`: add each fixed item as **[RESOLVED]** with a one-liner and a pointer
  to this doc's section.
- Task board: `TaskUpdate` each covered task → `completed` (the board is the live state the user
  watches).
- Worktree clean; commits scoped one-logical-change; push to `origin/main` (retry w/ backoff per
  repo convention).

**Round completion:** full-suite + mesh + templates baselines green; docs-archivist moves this file
to `work/archive/` (stamped with completion date and any amendments made during implementation);
capability-scribe pass over `docs/capability-matrix.md`.

**Amendment rule (repeat):** an implementing agent that discovers a design here doesn't survive
contact with the code does not silently improvise — it amends this document's section (stating
what and why) in the same commit as the divergent implementation, so the record and the code never
disagree.

---

## 4. Task-number index

| Task | Package | Decision in one line |
|---|---|---|
| #1 | WP-7c | Middleware passes ambient token to claim-check store (iface already accepts it) |
| #2 | WP-7a | `IHealthCheck.ExecuteAsync(CancellationToken)`; all implementers forward it |
| #3 | WP-1a | Reject `AllowedEmailDomains` (+groups) under `none` — satisfiability matrix |
| #4 | WP-1c | `POST /mesh/auth/logout`: CSRF-header, cookie sign-out, optional IdP end-session redirect |
| #5 | WP-1d | Pass `dispatchUrl` to `UseMeshUi` when dispatch enabled |
| #6 | WP-1a | Reject groups/role options under proxy-without-groupsHeader — matrix |
| #7 | WP-2b | Sort `required` ordinally; accept one-time hash drift |
| #8 | WP-4 | Null payload → empty response message (spec: `accepted → OK`) |
| #9 | WP-5a | `BENZ0001` build error on duplicate function name; never auto-rename |
| #10 | WP-5b | `Acked = true` only after settle succeeds |
| #11 | WP-5a | Generator gets a diagnostics table; `BENZ0002` for CosmosDb silent skip |
| #12 | WP-6a | `<T,Void>` → `InvocationType.Event`; stop swallowing `FunctionError` |
| #13 | WP-6b | AlreadyExists → DescribeExecution input compare; mismatch → Conflict |
| #14 | WP-6c | Sanitized-or-truncated names get a stable 8-hex SHA-256 suffix |
| #15 | WP-7e | Per-run state object; built Saga immutable + concurrency-safe (contract) |
| #16 | WP-3 | `ClaimToken` on Won; fenced `Complete/Release` return `bool` |
| #17 | WP-3 | `LeaseToken` on claim; fenced settle writes in all three stores |
| #18 | WP-7d | Evict on expired read + time-gated sweep on put; honest class doc |
| #19 | WP-1a | Reject `dispatch.enabled` under `none` — matrix |
| #20 | WP-1b | `requireHttpsMetadata` knob, default true; reject http authority otherwise |
| #21 | WP-2c | AzureMesh: wire refresh guard + sibling-parity README disclaimer |
| #22 | WP-2a | Overflowing duration → absent bound, never throw (P5) |
| #23 | WP-4 | Same adapter fix covers client-streaming call site |
| #24 | WP-8 | Implement `mandatory` via confirms + `BasicReturnAsync`; else remove flag (P6) |
| #25 | WP-9 | Union-aware walkers, direction table, both twins together |
| #26 | WP-7b | Delete duplicate internal timeout; processor wrap is the one timeout |
| #27 | WP-1a | Reject `dispatchRole` under `basic`; no `MESH_BASIC_ROLES` (rejected alt.) |
