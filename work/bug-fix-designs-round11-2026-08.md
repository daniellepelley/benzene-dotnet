# Round 11 review findings (2026-08)

**Status: ACTIVE — findings only, not yet fix-designed or implemented.** This round was explicitly
scoped by the user as review-only ("find issues... using multiple agents"), not a fix round. It
covers six areas of the codebase not deeply reviewed in rounds 1–10, run as six parallel review
agents, each in an isolated worktree detached at `ed25ca0` (the head of `main` after round 10's
fix round landed), with a ~100-minute budget each. Findings are tracked as task board **#121–#182**
(32 worth-fixing, 30 minor), plus a round-summary task **#183**. No fix packages have been designed
yet — that is deliberately left for a future round, pending the user asking for one.

Every finding below was **executed**, not just reasoned about: real hosts, real generated clients
compiled and invoked over real sockets, real mocked cloud SDKs (`IAmazonDynamoDB`, `IAmazonLambda`,
`IAmazonS3`, `k8s.IKubernetes`), a real loopback OIDC provider with genuine discovery/JWKS/token
exchange, and adversarial tokens/credentials driven through the real middleware. Each agent
cross-checked its findings against `work/outstanding-bugs.md` and `work/archive/*.md` before
reporting, to avoid re-reporting known issues; divergences from that cross-check are called out
explicitly where relevant (e.g. #2, #4, #6 below reference already-decided rulings from earlier
rounds that were not carried into the packages reviewed here).

All six agents confirmed a clean baseline before and after: `dotnet build Benzene.sln -c Release` →
0 errors, 988 pre-existing warnings; `git status` clean in every worktree; no probe files left
behind.

---

## §1 Headline results

- **No authentication bypass was found** in the adversarial auth review (Basic, OAuth2, Auth.Core,
  Mesh.Auth.Oidc). Timing safety, colon-splitting, algorithm/issuer/audience/lifetime pinning, cookie
  flags, and fail-closed defaults on empty requirement lists all held under attack. The real findings
  are DI-lifetime, fail-fast, and CSRF gaps in the OIDC layer specifically (§7).
- **A #20-class bug resurfaced twice, unfixed, outside where it was originally fixed.** Round 1's #20
  ("Mesh OIDC mode crashes with unhandled 500 when auth.oidc.authority isn't https") was fixed only in
  the Mesh Host's own `MeshAuthGate`, and never carried into `Benzene.Mesh.Auth.Oidc` or
  `Benzene.Auth.OAuth2`, both of which have the identical gap (#173, #174).
- **A decided ruling was silently contradicted.** Round 1's #4 ("no CSRF protection on `/logout`,
  deliberately... requires POST + header") is the host's answer to exactly the hazard the OIDC
  package's plain-GET logout reintroduces; the package's own CLAUDE.md still documents the old,
  now-contradicted position (#175).
- **Generated typed clients cannot call any endpoint with an enum property** — verified end-to-end
  with a real host and a real generated client, producing an actual HTTP 400 (#166). This is invisible
  to "does it compile" checks, which is exactly the gap prior rounds' codegen review couldn't see.
- **The Cloud Service Profile makes a false conformance claim in its default wiring** — R8 (trace
  propagation) reports satisfied when the trace middleware isn't actually wired, and the false claim
  reaches the descriptor a mesh collector reads (#167).
- **Several aggregation/discovery pipelines have no failure isolation**, so one denied provider, one
  throttled read, or one deleted Lambda function silently discards a healthy majority's results
  (#148–#150), continuing a pattern rounds 9–10 fixed repeatedly for health checks and settlement.
- **The rate limiter's advertised partitioning feature doesn't exist** — documented in four places,
  doesn't compile, and (more importantly) there is no per-caller partition key anywhere, so one
  abusive client can starve every legitimate one on a component whose whole purpose is DoS defense
  (#136).

---

## §2 Event Sourcing (`Benzene.EventSourcing` + `Benzene.EventSourcing.DynamoDb`) — #121–#132

Confirmed solid: multi-event transactional appends are genuinely all-or-nothing (every event in a
batch is conditioned, not just the first); `InMemoryEventStore` is thread-safe under concurrent
same-stream appends; `ReadAsync` pagination and ordering are correct; cancellation tokens are
forwarded on every SDK call; string payloads round-trip byte-exact including non-ASCII; culture has
no effect on version/timestamp parsing.

**Worth fixing:**
- **#121** — `AppendAsync` never verifies the stream is *at* `expectedVersion`, only that the target
  slots are free. An `expectedVersion` ahead of the real head silently writes a gapped stream and then
  *permanently livelocks it* for any correct writer that folds by reading the stream. A negative
  `expectedVersion` writes durable events `ReadAsync` can never return.
- **#122** — the blanket `catch (TransactionCanceledException)` translates throttling, capacity, and
  validation failures into `EventStoreConcurrencyException`, with a message that contradicts itself
  (compares the head to itself).
- **#123** — `EventStoreConcurrencyException` has no inner-exception constructor, so the real AWS
  failure is always discarded (blocks fixing #122 properly).
- **#124** — the post-conflict "actual version" diagnostic read runs on the *caller's* cancellation
  token with no guard, so a throttled read-back or a raced cancellation can replace a genuine conflict
  exception with an unrelated one, silently losing the conflict.
- **#125** — `InMemoryEventStore.AppendAsync` is not atomic across a batch; a mid-batch throw leaves a
  partial append, diverging from the DynamoDb store's all-or-nothing behavior — the store every test
  suite runs against behaves differently from the one that ships.

**Minor:** #126 (no fail-fast on table/key config), #127 (silent defaulting of unrecognized attribute
types, including a null `Payload` on a non-nullable property), #128 (empty-batch append skips the
concurrency check in DynamoDb only), #129 (no `ClientRequestToken` — ambiguous retries look like
conflicts), #130 (`InMemoryEventStore` ignores `CancellationToken`), #131 (`MaxEventsPerAppend`
enforced only in the DynamoDb store), #132 (`InMemoryEventStore` leaks an empty stream entry on every
rejected append against an unknown id).

**Just noting:** `EventEnvelope`/`StoredEvent` accept nulls with no guard; `response.Items` dereferenced
without a null check (safe today, AWSSDK v4 flips the default); empty `streamId` accepted in-memory but
rejected by real DynamoDB; the "serialization-agnostic (JSON, MessagePack, Avro)" doc claim is
misleading since `Payload` is `string` end-to-end; no dedicated cookbook page despite sibling
capabilities (Idempotency/Outbox/Claim Check) each having one.

---

## §3 Rate Limiting + Cache (`Benzene.RateLimiting`, `Benzene.Cache.Core`/`.Redis`) — #133–#147

**Rate limiting, worth fixing:**
- **#133** — the three convenience `UseXRateLimiting` overloads create a limiter nothing can ever
  dispose; verified via GC stress that 100/100 undisposed limiters (each rooted by its own
  auto-replenishment timer) survive forced collection — a leak per pipeline build, hitting per-test
  pipeline construction and hot-reload hardest.
- **#134** — a caller-disposed BYO limiter turns the *next* message into an unhandled
  `ObjectDisposedException`, crashing a protection middleware instead of making a deliberate
  fail-open/fail-closed choice.
- **#135** — `UsePayloadSizeRateLimiting` cannot bound memory: the synchronous cost delegate runs after
  the ASP.NET host has already buffered the whole body (`UseBufferedRequestBody()` is unconditional and
  runs before the caller's pipeline), so the limiter throttles only after the allocation it exists to
  prevent.
- **#136** — partitioned-limiter support is documented in four places (README, CLAUDE.md,
  `docs/rate-limiting.md`, capability matrix), doesn't compile (verified — `PartitionedRateLimiter<T>`
  cannot convert to `RateLimiter`), and doesn't exist: there is no partition key anywhere, so one
  limiter is shared by every caller. One abusive client denies every legitimate one — the opposite of
  the component's purpose.
- **#137** — 429 responses never carry `Retry-After`, even though the limiters supply the metadata
  (two sibling mesh middlewares already do this correctly).
- **#138** — rate-limit rejections are completely unobservable (no logger, no metric on the deny path).

**Cache, worth fixing:**
- **#139** — write-through failure handling is backwards: a cache-side exception *after* a committed
  DB write surfaces as a caller-visible failure (inviting a double-write retry); `InvalidateAsync`'s
  bool return is discarded twice; and Redis swallows its own `DEL` failures and returns `false` as
  success, so a failed invalidate is reported as success while the stale value keeps serving for the
  full TTL.
- **#140** — a cached `null` is a permanent miss, so negative caching is impossible (cache-penetration
  amplification) while `SetValueAsync(null)` returns `true` with no signal it's unusable. Also flags a
  now-stale `[DECISION]` entry in `work/outstanding-bugs.md:1401-1403` that describes pre-WP-X behavior.
- **#141** — the entire cache surface (10/10 members) is uncancellable, and `RedisCacheService`'s
  connect has no deadline — a hung Redis connection holds every in-flight request past client
  disconnect and host shutdown, contradicting the ambient-cancellation contract round 10 established
  for every other backend.

**Minor:** #142 (oversized-payload rejection message doesn't distinguish itself from a normal
throttle), #143 (negative BYO cost silently clamped to 0; cost-delegate exceptions bypass the limiter
entirely), #144 (per-call TTL unreachable through the documented cache-aside/write-through API), #145
(cache hard-wires `System.Text.Json`, ignoring the DI-registered `ISerializer`, no seam at all), #146
(`RedisCacheService.DisposeAsync` has no disposed flag — late setup after disposal leaks a connection),
#147 (`RedisMultiKeyActions` writes N keys sequentially — a partial write still reports success).

**Just noting:** cache-aside stampede is a documented non-goal in the agent-facing CLAUDE.md files
(same shape as #111, fixed elsewhere), but the caveat is absent from user-facing `docs/caching.md` and
the capability matrix, unlike every other row there.

---

## §4 Mesh discovery + catalog pipeline — #148–#157

**Worth fixing** (all five share one shape — a single failure aborting a batch of otherwise-healthy
work, the pattern rounds 9–10 fixed repeatedly elsewhere):
- **#148** — `MeshDiscoveryRunner` has no per-provider try/catch; one denied provider loses every
  other provider's results too, and the discovery host writes *no* registry document at all on
  failure, freezing the estate's registry forever.
- **#149** — an unguarded artifact-store read inside `MeshSnapshotBuilder` aborts the *entire*
  aggregation run on a single throttled read — verified: manifest, topics, topology, asyncapi, and
  every other service's snapshot lost because one drift-comparison read hiccuped. The sibling method
  in the same file already guards the equivalent read correctly.
- **#150** — `AwsLambdaDiscoveryProvider`'s `Task.WhenAll` over per-function `ListTags` calls means one
  deleted/inaccessible function loses every other function's results.
- **#151** — `FileSystemMeshArtifactStore` writes are not atomic (`File.WriteAllTextAsync`); verified
  23 torn reads under concurrency, including one that parses as valid-but-corrupt JSON. Not
  theoretical — it's the shipped Mesh Host's default artifact store, read by the same process that
  writes it on a timer.
- **#152** — pre-inlining schemas before comparison defeats `JsonSchemaComparer`'s `$ref`-name variant
  matching, so a pure `oneOf` branch reordering is published as a **breaking** change with two
  fabricated findings — the exact verdict a deployment decision is made on.

**Minor:** #153 (the shipped `MeshAggregateMessageHandler` bypasses the single-writer gate that exists
specifically to prevent this), #154 (permission vs. transient failures are indistinguishable in the
published catalog), #155 (Kubernetes lister ignores pagination — latent today, silent truncation if a
server ever returns a continuation token), #156 (a failed artifact write leaves a split catalog with
no run id to detect the mismatch), #157 (discovery has no per-provider timeout, unlike the aggregator's
own `PerServiceFetchTimeout`).

**Just noting:** `VariantKey`'s discriminator-fallback branch is dead code; `MeshHashing.ComputeHash`
allocates an undisposed `HMACSHA256` per call (harmless, finalizable); the two real cloud SDK adapters
(`KubernetesApiServiceLister`, `AzureArmResourceLister`) have zero test coverage, exactly where #155
lives; `MeshDiscoveryRunner` runs providers sequentially unlike the concurrent fan-outs elsewhere.

---

## §5 Less-common AWS/GCP transports — #158–#165

**Worth fixing:**
- **#158** — S3 object keys are never URL-decoded; any key with a space, `+`, `&`, `%`, or non-ASCII
  character reaches the handler in its raw encoded form, so `GetObjectAsync` returns `NoSuchKey`.
  Neither handled nor documented anywhere.
- **#159** — Pub/Sub: a `CloudEvent` with no `message` NREs in all three getters, and the NRE *escapes*
  `CatchExceptions = true` because the catch block itself dereferences `context.Message.MessageId`
  while logging — the guard designed to contain an exception throws a second one that replaces the
  real one. The equivalent hazard was already fixed on the AWS side (`SafeId`, SNS/SQS null-attribute
  hardening) but never reached Pub/Sub.
- **#160** — S3, DynamoDB, EventBridge, and Google Pub/Sub's DI extensions all use plain `AddScoped`
  instead of `TryAddScoped`, silently shadowing a user's earlier registration. The systemic fix for
  this exact defect (`work/archive/customization-robustness-review-2026-08.md`) landed on nine other
  packages and missed these four (plus, per the agent's note, `Benzene.Aws.Lambda.Kafka` and all
  `Benzene.Azure.Function.*` packages — outside this round's scope but the same root cause).

**Minor:** #161 (Pub/Sub outbound converter has no attribute-limit guard, unlike SNS's
`GuardAttributeLimit`), #162 (Kinesis's resume-point computation runs outside `CatchExceptions`'
protection, so a malformed record's NRE loses partial-resume information), #163 (EventBridge body
getter mishandles explicit JSON `null` detail and double-serialized string detail), #164 (GoogleCloud
Functions HTTP inherits the AspNet adapter's header-casing/null-`Method` defect already fixed for API
Gateway in #105), #165 (SNS/Pub/Sub headers dictionaries use an inconsistent comparer depending on
whether attributes were present, and a cookbook's case-insensitivity claim is false for five
transports).

**Just noting:** Pub/Sub ordering keys are invisible in both directions and undocumented; Pub/Sub body
getter is a lossy UTF-8 decode with no raw-bytes escape hatch; `DynamoDbAttributeValueConverter` is
completely undefensive against malformed attribute values (though batch isolation itself is fine);
EventBridge's `_benzeneHeaders` leaks into the handler body with non-string values silently dropped; S3
headers use inconsistent bare-camelCase naming vs. DynamoDB/EventBridge's prefixed convention; nothing
documents the SNS→SQS raw-message-delivery requirement.

**Verified correct, no finding:** DynamoDB `REMOVE`/image-fallback mapping, Kinesis base64 decoding
(exactly once, lossless, no compression assumption), SNS raw-message-delivery structurally absent for
Lambda, EventBridge `detail-type`/`source` extraction, DynamoDB batch isolation of a bad record.

---

## §6 Spec/descriptor/CloudService/Probe pipeline — #166–#171

**Worth fixing:**
- **#166** — generated typed clients (via `MessageClientSdkBuilder` **and** the shipped `benzene
  build` CLI) turn every enum property into an empty C# class with no members. Verified end-to-end
  with a real host and a real generated client: the client sends `"status":{}` on the wire and the
  server rejects it with HTTP 400 — reproduced identically even with a `JsonStringEnumConverter`
  applied, so this is a generator gap, not a "can't know the converter" limitation. Every service with
  an enum on a request DTO ships a client that cannot call it.
- **#167** — `CloudServiceProfileReport` reports R8 (trace context propagation) satisfied whenever
  mesh is enabled, but the trace middleware is only actually wired when a collector/exporter is also
  configured. Verified: the *default* wiring (`UseBenzeneCloudService("svc")`, mesh on, no collector)
  claims R8 satisfied while `MeshSpan.Current` is genuinely null — a false claim that reaches the
  descriptor a mesh collector reads.
- **#168** — `benzene diff` never recurses into `additionalProperties`, so a breaking change (type
  change + new required property) inside a `Dictionary<string, T>`-shaped schema passes the CI gate as
  "No changes" — verified via the real CLI. Distinct from the already-tracked `[DECISION]` entry about
  `Enum`/`Nullable`/facet classification gaps; this needs no new change kinds, just the missing
  recursive call the `Items` branch already models.
- **#169** — the derived spec's schema property names are PascalCase while the wire, the spec's *own*
  example block, and the sibling `.service.json` from the same build are all camelCase — verified via
  one `benzene-descriptor --emit both` run producing self-contradictory output. Three downstream
  consumers already independently patch around this; none of them is the root cause. Matters most for
  the planned non-.NET client generators, which would emit the wrong casing verbatim.

**Minor:** #170 (topic-scoped client generation emits the entire service catalogue's DTOs instead of
the narrowed reachable set, also making the drift-detection hash unstable), #171 (`--version-scheme` is
validated at build time then never carried onto the emitted descriptor).

**Just noting:** `Benzene.Descriptor`'s "built, non-running service" claim verified true via `strace`
(zero TCP binds); `CloudServiceProbe` verified genuinely independent of the self-report; `ContractHash`
verified stable across key order and culture; path relocation (`.WithHealthPath`, `.WithoutMesh`)
reported honestly. Adjacent non-defects: the mesh `ServiceDescriptor` loses some contract fidelity
(polymorphic shape, int64 format) that the OpenAPI spec keeps — enum loss is documented, the other two
are not; a couple of `CSharpTypeName` nullable-handling inconsistencies with no wire impact.

---

## §7 Auth adapters — #172–#182

**No authentication bypass found.** The full adversarial matrix (21 crafted JWTs, `alg:none` with and
without a trailing dot, wrong-key HMAC forgery attempts, colon-splitting, timing, email-allowlist
homoglyph/subdomain/multi-`@` tricks, cookie flags, `ReturnToValidator` open-redirect attempts) held.
All findings of substance are in the OIDC/mesh layer.

**Worth fixing:**
- **#172** — `OidcSessionGateMiddleware` is registered as a singleton but captures a *scoped*
  `IOidcSessionSink` in its constructor — verified it keeps the first scope's instance forever across
  fresh scopes. Consequence: every dispatch after the first request in a container's life is 403'd
  with the identity lost from the audit trail. This is the exact singleton-captures-scoped anti-pattern
  Benzene's own DI-adapter code already documents as a previously-fixed hazard elsewhere.
- **#173** — `MeshOidcOptions.Validate()` accepts a non-HTTPS `Issuer` with `RequireHttpsMetadata=true`,
  crashing OIDC discovery as an unhandled 500 at request time. This is round 1's #20, fixed only in the
  Mesh Host's `MeshAuthGate` and never carried into this package.
- **#174** — `Benzene.Auth.OAuth2` has the identical gap for its Authority/JwksUri, plus a
  length-only `Validate()` that accepts empty/`"*"` issuer entries and `ValidAlgorithms=["none"]`.
- **#175** — `OidcLogoutMiddleware` is a bare GET with no CSRF defense — verified a cross-site GET
  signs the victim out. This directly contradicts round 1's #4 ruling, which the Mesh Host's own
  `MeshAuthGate.HandleLogoutAsync` implements correctly (405 on GET, requires POST + header); the
  package's CLAUDE.md still documents the old, superseded position.
- **#176** — `MeshAuthGate`'s proxy-trust check rejects IPv4-mapped IPv6 peers (`::ffff:10.0.0.5` ≠
  `10.0.0.5`), breaking `auth.mode: proxy` on any dual-stack listener (fail-closed, not a bypass, but a
  real operability gap that invites widening the allowlist).
- **#177** — `MeshOidcOptions.SigningKey` is checked for byte length only; a 32-character repeated
  character passes. That key signs a session token that is a deterministic function of `{Email, Exp}`
  with no randomness, so a guessable key is complete session forgery.

**Minor:** #178 (logout is client-side only, no `jti`, revocation is unexpressible not just
unimplemented — and undocumented as a decision), #179 (`RequirePolicy` throws its wiring error
per-request instead of at wire-up), #180 (post-login `returnTo` is lowercased, breaking case-sensitive
deep links), #181 (`MeshAuthGate.IsPermitted` admits an empty-local-part address and trims
asymmetrically), #182 (two role-claim readers in the repo disagree on JSON-array claim expansion).

**Just noting:** `IBasicAuthCredentialValidator` gives implementers no constant-time-comparison
guidance (measured — no exploitable timing oracle found today, but not documented either); no PKCE in
the mesh OIDC flow (nonce omission is a documented decision, PKCE isn't mentioned and is nearly free to
add); `RequestUrl.BuildBaseUrl` trusts client-supplied Host/`X-Forwarded-Proto` but the IdP's
`redirect_uri` allowlist catches it in practice; `MeshOidcOptions.CookiePath` isn't validated (a
malformed path silently scopes the cookie, causing a login loop); package CLAUDE.md drift on the
logout redirect target.

---

## §8 Next steps

This document is the review record for task board #121–#183. Per the user's explicit framing, this
round concludes here — no fix packages have been designed, and no code was changed by any review
agent (each confirmed `git status` clean before finishing). If a fix round is wanted, the natural next
step mirrors rounds 7–10: group these findings into work packages by shared file/blast-radius (the
groupings in §2–§7 above are a reasonable starting point — note some findings block others, e.g. #123
before #122, and #148/#149/#150/#157 share one loop shape and one worktree), one agent + one worktree
per package, orchestrator-merged sequentially, full baseline re-verified centrally after the last
merge.
