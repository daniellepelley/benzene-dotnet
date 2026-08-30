> ARCHIVED 2026-08-30: actioned. The round-14 review record (task board #204–#224: the mesh UI
> client-side review, plus early-round (1 and 9) re-passes at the mature "execute real adversarial
> probes" standard). Its worth-fixing findings landed as part of the rounds 12–14 fix plan: §1
> (#204–207) as WP-O (#204 fixed in-repo; #205–207 dispositioned `[UPSTREAM]`, not fixed here), §2
> (#208–209) as WP-K, §3 (#210–213) as WP-L/WP-M, §4 (#214–223) as part of WP-P — see
> [`bug-fix-plan-rounds12-14-2026-08.md`](bug-fix-plan-rounds12-14-2026-08.md) for the fix plan and
> landing commits. Per-finding summaries live in `work/outstanding-bugs.md` (search "round 14"), each
> pointing back here for the full record.

# Round 14 review findings (2026-08)

**Status: ACTIVE — findings only, not yet fixed.** With every `src/` package now touched by at least
one prior round, this round shifted to two things: genuinely untested ground that no C#-package-based
review could ever reach (the mesh UI's client-side code), and areas last reviewed in early rounds (1
and 9) before the "execute real adversarial probes" standard matured — treated as full first-rigor
passes, not touch-ups. 4 parallel agents, ~60-minute budget each, each in an isolated worktree
detached at `e33c810`. Findings are tracked as task board **#204–#223** (10 worth-fixing, 10 minor),
plus a round-summary task **#224**.

---

## §1 Mesh UI client-side — #204–#207

The headline finding here isn't a code bug — it's a doc that actively misleads whoever reads it next.
`mesh-ui.html`/`mesh-spec-ui.html` are not hand-written vanilla JS as `Benzene.Mesh.Ui/CLAUDE.md`
extensively describes; they're a minified React + Redux Toolkit build vendored verbatim from an
external `benzene-ui` repo, kept in sync by a CI drift-check that explicitly says "never hand-edit."
The actual security surface (XSS via unsafe-HTML sinks, URL-scheme handling, CSRF header contracts,
double-submit guards on the three write actions) came back clean — React's default escaping plus
already-fixed prior work (#36's logout-failure fix, the `safeHttpUrl` scheme gate) hold up.

**Worth-fixing:**
- **#204** — `Benzene.Mesh.Ui/CLAUDE.md` doesn't mention the vendoring relationship at all; it
  documents features and conventions absent from the actual shipped bundle. An agent trusting this
  doc (which the harness instructs following verbatim) would plausibly hand-edit the generated file
  directly — exactly what the drift-check exists to prevent.
- **#205** — the Refresh button has no confirmation step despite the package's own doc calling it
  "real money per click" (fans out to every service in the mesh on every press), unlike the dispatch
  Send button, which requires an explicit checkbox.

**Minor:**
- **#206** — the Sign-out button has no pending/disabled state, unlike Refresh and Send; a rapid
  double-click can fire two concurrent logout requests.
- **#207** — Sign-out's `fetch()` doesn't pass `credentials:"same-origin"` explicitly, unlike the
  other two write-action helpers (not an actual bug — the spec default is same-origin — but an
  inconsistency worth normalizing).

---

## §2 Saga + ClaimCheck (round-1 vintage) — #208–#209

Round 1's fixes (#15 SagaStep concurrency, #1/#18/#62 ClaimCheck cancellation/eviction) all re-verified
intact under genuinely adversarial conditions (200 concurrent+retrying runs, 0 cross-contamination;
500K-entry sweep cost measured directly). Pushing past them found two real gaps in Saga's own
reliability story:

**Worth-fixing:**
- **#208** — a saga-state-store failure aborts the run with **zero rollback attempt**, silently
  breaking the class's own documented "all-or-nothing" guarantee. Verified: a state-store exception
  right after a real effect-producing stage completes propagates raw out of `RunAsync` — the
  registered `Compensate` for that stage never runs, and the caller gets a thrown exception instead of
  even a `PartiallyRolledBack` result.
- **#209** — when two steps in the same stage fail concurrently (a normal production scenario — two
  downstream calls both timing out), `SagaResult` only ever surfaces one of them via
  `Failure`/`FailureException`; the other has no representation anywhere on the public result, unlike
  `CompensationFailures`, which is a full list.

**Just-noting:** Saga has no `CancellationToken` support anywhere in its public API (asymmetric with
ClaimCheck's ambient-cancellation pattern, but steps are user delegates that can bake in their own
tokens); `InMemoryClaimCheckStore`'s sweep cost measured at 500K entries (19ms blocking lock) — already
an explicitly documented trade-off, not a defect.

---

## §3 Autofac + CodeGen.ApiGateway/Markdown (round-9 vintage) — #210–#213

Round 9's fixes (#82–87) all re-verified intact under real, executed conditions (32-way concurrent
resolution, live TryAdd collision, mixed singleton+scoped `GetServices<T>()`). Pushing past them found
a real container-choice asymmetry and two fresh reproductions of the exact bug class #87 fixed,
reached through different inputs:

**Worth-fixing:**
- **#210** — the Autofac adapter throws on a **closed** generic `Type` where the Microsoft adapter
  succeeds, because the generic-routing check across six methods tests `IsGenericType` (true for both
  open and closed generics) instead of `IsGenericTypeDefinition`. A discovered handler class that
  happens to be a closed generic works under Microsoft DI and throws under Autofac.
- **#211** — `ApiGatewayBuilderV1`'s duplicate-route guard doesn't case-fold `Method`, unlike the
  production `ReflectionHttpEndpointFinder` it's meant to mirror (which explicitly does, with a
  comment about this exact risk). Two topics mapped to `"GET"` and `"get"` for the same path pass the
  duplicate check silently and then both emit `get:` under the same path block — the identical
  duplicate-key YAML shape #87 fixed, reached via verb casing instead of identical-casing.
- **#212** — unescaped string interpolation into generated YAML produces genuinely invalid or
  semantically-corrupted output for adversarial topic/path content (a `"` in a topic name breaks the
  `summary:` scalar; a `: ` in a path segment survives title-casing into an invalid unquoted tag
  sequence item) — same root cause as #87, different trigger.

**Minor:**
- **#213** — `MarkdownTypeBuilder.MapProperty` NREs on an array schema with `Items == null`; not
  reachable through Benzene's own `SchemaBuilder` but the method is public and callable with any
  hand-authored schema, and a sibling method in the same class already null-checks the equivalent
  case.

---

## §4 Nine previously-unreviewed example apps — #214–#223

`App`, `Asp`, `Grpc`, `Versioning`, `Google`, and `OpenTelemetry` all came back genuinely clean —
each was built *and run*, with live requests/tests confirming they do what they claim (including
OpenTelemetry actually emitting real `Activity`-based spans, not just claiming to). `GoogleCloudMesh`,
`Outbox`, and `Kafka` did not:

**Worth-fixing:**
- **#214** — `examples/GoogleCloudMesh/Mesh/Startup.cs:48` is a genuine **build error**
  (`MeshServiceRegistry.FromEnvironment()` doesn't exist; the example's own `MeshRegistry` class has
  it), directly contradicting the example's README claim that "the whole solution builds," and would
  fail the real `gcloud functions deploy` step in `.github/workflows/mesh-example-google-cloud-deploy.yml`.
- **#215** — `examples/Outbox` is not a member of any solution file at all (the same pattern round
  12 found for `Cqrs`/`K8sTransports`, #193) — the project itself is solid (builds clean standalone,
  runs and demonstrates exactly what its README narrates), but nothing in the documented build gate
  ever compiles it.
- **#216** — `examples/GoogleCloudMesh` is entirely undocumented in `examples/CLAUDE.md`, unlike every
  sibling mesh example — combined with #214, nothing in the documented dev loop ever builds it, which
  is likely why the typo went unnoticed.

**Minor:**
- **#217** — `examples/Kafka/docker-compose.yaml` pins `confluentinc/cp-kafka:latest` in a ZooKeeper
  topology; Docker Hub's tag API confirms `latest` currently tracks a Confluent Platform line that
  dropped ZooKeeper support entirely, while the example's own test-harness compose file correctly
  pins the last ZK-compatible version. Flagged as "very likely broken" on strong circumstantial
  evidence (no Docker daemon available to fully confirm).
- **#218** — `examples/Asp/Startup.cs:52` hardcodes an Application Insights instrumentation key in
  source rather than config — low severity, but teaches a bad pattern in one of the most-copied
  examples in the repo.
- **#219** — `examples/Asp`'s demo JWT issuer hardcodes `Issuer`/`JwksUri` to `http://localhost:5000/`
  with no hint given on failure if the app runs on a different port (verified: an opaque 401 with no
  explanation).
- **#220** — `examples/App/Benzene.Examples.App.Data` is a dead, orphaned project referenced by
  nothing, with a stale pre-split namespace and an EF Core/Npgsql version out of support since 2024.
- **#221** — a cosmetic CS8632 nullable-annotation warning in `GoogleCloudMesh/Shared/MeshServiceWiring.cs`.
- **#222** — `examples/Asp/config.json` ships a dummy DB connection string with a plaintext placeholder
  password that nothing actually reads — fake, but teaches a bad pattern.
- **#223** — `examples/Asp/Startup.cs` emits an `ASP0001` middleware-ordering warning, harmless here
  but copied verbatim by anyone using the file as a template.

---

## §5 Next steps

Per the established cadence, this document is the review record for #204–#224. No fix packages have
been designed and no code was changed by any review agent. The user has indicated a separate agent
will pick up fixes from the task board going forward — this session's role continues to be finding and
recording issues, not fixing them, unless asked otherwise.
