# New mesh sources — design decisions (D1–D5)

**Status:** decisions made; this is the buildable brief `work/enterprise/slice-4-sources.md` §5
asked for. Supersedes nothing; `slice-4-sources.md` stays as the record of *why* these questions
existed.
**Owner:** `observability-product-owner` (D1, D2, D3, D5), with `mesh-product-owner` sign-off on
scope (D1 port assignment, D3 defer decision).
**Depends on:** `work/enterprise/slice-1-config-schema.md` (the `"usage": [{ "source": … }]`
catalog this plugs into).

Source material read for this note: `work/enterprise/slice-4-sources.md`, `work/enterprise/README.md`,
`work/otel-fleet-adapter-scope.md`, `docs/mesh-usage-feed.md`,
`src/Benzene.Mesh.Contracts/IMeshUsageSource.cs`, `src/Benzene.Mesh.Usage.CloudWatch/CloudWatchUsageOptions.cs`,
`src/Benzene.Mesh.Usage.ApplicationInsights/ApplicationInsightsUsageOptions.cs`,
`src/Benzene.Mesh.Fleet.Tempo/{CLAUDE.md,TempoTraceSource.cs,TempoTraceSourceOptions.cs}`,
`src/Benzene.Mesh.Tracing.Tempo/PrometheusQueryClient.cs`, `src/Benzene.Mesh.Collector/CLAUDE.md`,
`work/enterprise/slice-2-auth.md` (the `*EnvVar` secret precedent D4 reuses).

---

## D1 — which port does each backend implement

"Add Elasticsearch" and "add an OTel store" are both resolved by asking what each backend actually
*is*, not by treating either as one adapter:

| Backend | `IMeshUsageSource` | `IMeshTraceSource` | `IMeshIssueSource` |
|---|---|---|---|
| **Prometheus** (the OTel-compatible store named in the brief) | **Yes — build it.** Prometheus stores metrics; the `benzene.messages.processed` counter is exactly what it's for. | **No.** Prometheus does not store spans. An OTLP trace store already has a name and a package: Tempo (`Benzene.Mesh.Fleet.Tempo`, TraceQL). "OTel store" collapses to "the metrics half is Prometheus, the trace half is already shipped" — it is not a second thing to build. | No. Out of scope for the same reason as usage: no span/log storage. |
| **Elasticsearch** | **Yes — build it**, reading OTel metric documents (however they got into an ES index — see the caveat below). | **Deferred**, not rejected. Only relevant if traces are shipped to Elastic APM, which is a *different* storage/query shape (ECS `traces-apm*` data streams + the APM Server's own query surface) from every existing `IMeshTraceSource` (X-Ray segments, TraceQL, Jaeger's API) — a fourth trace-source adapter with no shared template, not a variant of one that exists. Nobody has asked for it yet; build it as its own increment when someone does. | **Deferred — see D3.** ES is log-shaped storage and the most plausible eventual home for issues, but the port doesn't exist and building it to fit one backend is exactly what the brief's "Do NOT" section forbids. |

**Resolution: this slice ships two `IMeshUsageSource` adapters, Prometheus and Elasticsearch — not
a trace source, not an issue source.** X-Ray/Tempo/Jaeger already cover the trace side; nothing in
the brief's research says a fourth trace backend is wanted. That also directly narrows D5: the
"unverified template" risk the brief is worried about is a trace-source risk (TraceQL/OTLP shape),
and neither new adapter in this slice is a trace source, so that specific risk does not transfer
onto this slice's deliverable as directly as the brief assumes (more in D5).

One naming nuance worth pinning here because it *will* be invented wrong otherwise: **Prometheus
mangles the OTel instrument name.** The OpenTelemetry-to-Prometheus exposition mapping (a
documented, stable OTel spec transform, not a guess) turns dots into underscores and appends
`_total` to a monotonic counter, so `benzene.messages.processed` arrives at a Prometheus endpoint
as `benzene_messages_processed_total`, tags→labels unchanged (`topic`/`transport`/`result`). The
Prometheus adapter's default `MetricName` must be `benzene_messages_processed_total`, not the dotted
form the CloudWatch/App Insights adapters default to — copying their default verbatim would be a
silent no-op query (metric not found → empty usage feed, no error). Make it a configurable
`MetricName` exactly like the two shipped adapters, but get the *default* right, because "the
default is wrong" is invisible until someone diffs a live query.

---

## D2 — the cross-port usage-metric-name convention

**Agree with the brief's position (not spec material), and go one step further on *where* it lives.**

Why not the spec: `docs/mesh-usage-feed.md` already states the usage feed is "an observability
concern, not a Cloud Service spec concern" — it adds no endpoint, and a service that never wires
`UseBenzeneMetrics()` is still a fully conformant Benzene service. `docs/specification/` is reserved
for the *observable, conformance-fixture-backed* contract (wire shapes, status vocabulary, mesh
wire topics). The metric name/tag set is neither: it's a convention about what a service *may*
export to a telemetry backend, consumed by an optional adapter, never asserted by a conformance
fixture. Pinning it in `docs/specification/` would also contradict the house rule in this repo's
`work/enterprise/README.md` ("Do not put mesh-server configuration … into the language-neutral
spec") in spirit even though it's not literally server config — same smell, same answer.

But "not spec" isn't the same as "lives in one language's private docs forever," and that's the gap
in the brief's framing. Today the only place this convention is written down is
`docs/mesh-usage-feed.md` **in this .NET repo**. That is fine as long as .NET is the only consumer,
but the moment a Prometheus/Elasticsearch adapter is meant to also work for a Go, TypeScript, or
Python service's counters, the convention has become a **cross-language interop requirement wearing
a documentation-only costume** — exactly the kind of thing `work/README.md`'s own rule 5 ("one home
per document... two copies diverge silently; we have already proved this") exists to catch. This
repo has already moved several such documents (`benzene-naming-principle.md`,
`benzene-headers-design.md`, …) out to the sibling spec repo's `work/` folder for precisely this
reason: their subject outlived being one language's document.

**Decision:** the instrument names, tag keys, and the `success`-collapse/failure-itemization rule
belong in a **living cross-language document in the `Benzene` (spec) repo's `work/`**, sibling to
`mesh-enterprise-readiness.md` — not in `docs/specification/` (it's not conformance-tested), and not
solely in `benzene-dotnet/docs/mesh-usage-feed.md` (it can't stay a private convention once a second
language port needs to match it byte-for-byte). Concretely, propose (not executed by this note,
since it touches a sibling repo — flagged as the follow-up action):

- A new `Benzene/work/mesh-usage-metric-convention.md`, owned by `observability-product-owner`,
  stating: instrument names (`benzene.messages.processed` counter, `benzene.message.duration`
  histogram), the three tags (`topic`/`transport`/`result`), the `result` collapse rule (success
  collapses to `success`; failure is itemized by wire status; `exception` on a thrown pipeline;
  `"<missing>"` when unrecorded), and **the per-exporter name transform each metrics-store adapter
  must apply** (Prometheus's dots→underscores + `_total`; CloudWatch/EMF and Azure Monitor keep the
  dotted name verbatim — see the D1 note above, generalized).
- `benzene-dotnet/docs/mesh-usage-feed.md` keeps its .NET-specific content (which package emits it,
  `UseBenzeneMetrics()`, the shipped-adapter list) but its §1 becomes a pointer to the cross-repo
  convention doc rather than defining the convention itself — the same demotion this repo already
  did for the documents rule 5 names.
- **Before treating the name as pinned across ports, audit it.** Nobody has confirmed Go/TypeScript/
  Python actually emit `benzene.messages.processed` with these three tags — the brief's whole worry.
  This is a one-time check (read each port's diagnostics package, or ask each port's owner) that
  should happen *before* the convention doc is written as fact rather than aspiration. If a port
  already emits something different, that is a breaking-alignment decision belonging to that port's
  owner, not something this slice can quietly paper over by choosing the .NET name as canonical.

This is design/documentation follow-up, not code, and not this repo's file to write (it lives in
`Benzene`, the spec repo) — recorded here as the action, owned by `observability-product-owner`,
to be done before or alongside the Prometheus adapter, not after.

---

## D3 — does `IMeshIssueSource` get built now

**No. Explicitly deferred, not by accident.**

Reasons, concretely:

1. **Issues today are a *push* concept with collector-side semantics that have never been
   generalized.** Reading `src/Benzene.Mesh.Collector/CLAUDE.md`: fingerprinting, delta-merge
   (`count += delta`, `firstSeen = min`, `lastSeen = max`), the "issues survive re-registration —
   observations, not claims" rule, and the bounded eviction policy are all decisions
   `MeshCollectorStore.AddIssues` makes as the *only* implementation. There is no `IMeshIssueSource`
   port to implement because nobody has yet answered what a *queried* (pull) issue feed means: does
   a query-time backend re-derive fingerprints from raw error documents each call, or does it need
   its own persistent aggregation? Does "resolved" still mean "quiet `lastSeen`" when the backend is
   Elasticsearch and not an in-memory ring with its own clock? These are product/design questions,
   not implementation details, and `slice-4-sources.md`'s own "Do NOT" section says not to let the
   port arrive by accident to satisfy one adapter.
2. **`CompositeMeshFleetReadModel` already has a designed answer for "no issue feed":** it marks
   issues as a permanently missing feed (`work/enterprise/README.md`'s deferred-items table confirms
   this is a known, accepted gap, not a bug). Adding Elasticsearch as a usage source does not make
   that gap worse — the composite plane already degrades honestly today.
3. **Elasticsearch is a plausible, not obvious, first implementer.** It's log-shaped storage, so
   issue-from-logs is a reasonable direction, but "plausible" isn't "scoped" — the shape of an issue
   query (an ES aggregation over an error-log index? a saved search? Elastic's own APM error-grouping
   feature, which does its own fingerprinting differently from `MeshCollectorStore`'s?) is exactly
   the kind of invented-answer risk this whole brief exists to avoid inventing under code-writing
   pressure.

**Action, not scope:** `IMeshIssueSource` gets its own design note (mirroring this one) before it is
built for *any* backend, Elasticsearch included. It is not part of this slice's first increment or
its immediate follow-up increment.

---

## D4 — referencing a secret from `mesh.json` without putting it in `mesh.json`

**Convention: reuse and generalize the pattern slice 2 already established for OIDC, don't invent a
new one.** `work/enterprise/slice-2-auth.md` already has the answer for `clientSecretEnvVar` (§2.1):
*the config key holds the **name** of an environment variable, never the secret value.* That
precedent should become the house-wide rule for every future source, not a one-off for OIDC:

- **Every secret-bearing option is named `<Thing>EnvVar` and holds an env-var name.** For the
  Prometheus/Elasticsearch usage sources specifically:
  - `apiKeyEnvVar` — an ES/Prometheus API key sent as a header (e.g. `Authorization: ApiKey …` for
    Elasticsearch, or a reverse-proxy-issued key in front of a self-hosted Prometheus).
  - `bearerTokenEnvVar` — a bearer token (e.g. Grafana Cloud's Prometheus remote-read token, or an ES
    service-account token).
  - Basic auth, if it comes up, follows `slice-2-auth.md`'s existing `MESH_BASIC_USER` /
    `MESH_BASIC_PASSWORD` shape (two env-var-name keys, `usernameEnvVar`/`passwordEnvVar`), not a
    combined "credentialsEnvVar".
- **Slice 1's `Dictionary<string,string> options` shape already carries this for free** — no schema
  change needed. `"usage": [{ "source": "prometheus", "options": { "prometheusUrl": "…",
  "bearerTokenEnvVar": "MESH_PROMETHEUS_TOKEN" } }]` fits the existing per-source options bag exactly
  as CloudWatch's `namespace`/`windowHours` do today.
- **The registrar (`MeshSourceRegistrar`, slice 1 §1.2) resolves the env var at startup, not at query
  time**, and fails fast (matching slice 1's "unknown names fail fast, listing valid values" rule) if
  a configured `*EnvVar` key names a variable that isn't set — a source that silently sends an empty
  `Authorization` header on every poll is a worse failure mode than refusing to start.
- **No secret-store URI scheme (`vault://…`, `awssm://…`) for this slice.** It was considered — it's
  a real pattern elsewhere in the industry — but there is no existing Benzene precedent for it (a
  repo-wide grep found none), it would need its own resolver-plugin design (which secret backends?
  who authenticates to *them*?), and CloudWatch/X-Ray's own answer to authentication is "there is no
  secret, use the ambient credential chain" — this repo has never needed a secret-store abstraction
  yet. Adding one now, for two adapters, ahead of a real second requirement, is speculative. If a
  customer specifically needs Vault/AWS Secrets Manager, that's a real future ask with its own
  design note; env-var-name reference (which every secret-store's own sidecar/CSI-driver pattern can
  populate into an env var anyway) covers the actual requirement today.

This also directly answers the brief's framing: *"the convention it picks will be the one every later
source inherits."* Good — that's exactly why it should be the same `<Thing>EnvVar` shape slice 2
already uses, not a second convention living next to it.

---

## D5 — does the unverified-Tempo caveat block this slice

**Partially agree, but the brief's own risk doesn't fully transfer to this slice, and there's a
cheaper fix than blocking.**

Two separate risks were being conflated:

1. **The TraceQL/OTLP trace-API shape** (`Benzene.Mesh.Fleet.Tempo`'s `CLAUDE.md`: "shipped-but-unverified
   against a live Tempo trace API"). Per D1, this slice ships no new trace source, so this specific
   risk does not compound here. It would matter for a *future* Elasticsearch/Elastic-APM trace source
   (already deferred, D1) — flag it as a precondition for that future increment, not this one.
2. **The PromQL query shape** (`Benzene.Mesh.Tracing.Tempo`'s `PrometheusQueryClient`, used to read
   Tempo's metrics-generator, also carried unverified). **This one does transfer directly** — the
   Prometheus usage adapter this slice adds queries a PromQL-compatible endpoint the same way,
   summing a counter over label dimensions. Building it "by analogy" with the same
   never-run-against-a-live-backend pattern really would double the exposure the brief warns about,
   just on the metrics side rather than the trace side.

So the real, narrower finding: **the Prometheus usage adapter must not ship with the same
verification gap Tempo's PromQL client has — and it doesn't need to, because the reason Tempo's
verification was never done doesn't apply to Prometheus.** The CLAUDE.md caveat blames "the egress
limitation that blocked live-verifying Benzene.Mesh.Tracing.Tempo" — i.e., the dev environment when
that adapter was written couldn't reach an external SaaS Tempo. That's a reason to avoid depending on
a *hosted* backend, not a reason live verification is impossible in general. **Prometheus (and Tempo)
both run as a single, license-free Docker container with no external dependency** — a CI job can
start one locally with no egress at all beyond pulling the image, which is normal, unblocked CI
traffic.

**Recommendation — cheaper than blocking, and it closes more than it opens:**

- **Gate the first increment on a live-container integration test**, not on a design pause: stand up
  a real `prom/prometheus` container in CI, push a synthetic `benzene_messages_processed_total`
  series with `topic`/`transport`/`result` labels (via Prometheus's remote-write or by scraping a
  tiny stub exporter), and run `PrometheusUsageSource` against it for real. This is a small, one-time
  CI job, not a standing blocker, and it retires the exposure before the adapter ships rather than
  after.
- **That same test infrastructure cheaply de-risks the *existing*, still-open Tempo caveats too** —
  running `grafana/tempo` alongside `prom/prometheus` in the same CI job (or a follow-on one) would
  verify `Benzene.Mesh.Tracing.Tempo`'s PromQL shape and (separately) `Benzene.Mesh.Fleet.Tempo`'s
  TraceQL shape against real backends. Recommended as its own small follow-up ticket referenced from
  here — it is a fix to *already-shipped* adapters, not new slice-4 scope, so it should not gate this
  slice, but it should happen, and it's now demonstrably cheap rather than a standing "we can't
  verify this" caveat.
- **Do not build the Elasticsearch usage adapter by analogy with anything unverified** — see the note
  under "later" below; it has its own, different, and separately-unresolved schema question (D2's ES
  caveat), so "verify Prometheus, then copy the pattern to Elasticsearch" is exactly the copy-the-
  unverified-template mistake the brief is warning against, applied to the second adapter instead of
  avoided.

---

## Projects for the first increment

In scope now:

1. **`Benzene.Mesh.Usage.Prometheus`** (new package) — `IMeshUsageSource` only (D1). Three-file shape
   matching the shipped adapters:
   - `PrometheusUsageOptions(prometheusUrl, timeWindow = 24h, metricName = "benzene_messages_processed_total")`
     with settable `TopicLabel`/`TransportLabel`/`ResultLabel` (defaults `"topic"`/`"transport"`/`"result"`)
     and secret options per D4 (`ApiKeyEnvVar`/`BearerTokenEnvVar`, both optional/nullable — an
     unauthenticated local Prometheus is a legitimate, common target).
   - `PrometheusUsageSource : IMeshUsageSource` — one `query_range`/instant-query PromQL request
     (`sum by (topic, transport, result) (metricName[window])`-shaped), mapped to `MeshUsageEntry`
     rows exactly like CloudWatch/App Insights (`service` reported absent — Prometheus has no service
     dimension on this counter either, same honest-degradation call the two shipped adapters make).
   - `Extensions.AddPrometheusUsage(options)`.
   - Reuses the HTTP+JSON PromQL request/response handling already proven in
     `Benzene.Mesh.Tracing.Tempo/PrometheusQueryClient.cs` — worth factoring into a small shared
     internal helper (or at minimum copying its shape deliberately) rather than reinventing PromQL
     JSON parsing a second time in the same solution.
2. **The D4 secret-reference convention**, applied to `PrometheusUsageOptions` and documented in
   `deploy/Mesh/README.md`'s per-source least-privilege matrix (slice 1 §1.6) — this is the first
   source that actually needs it, so it should land with the source that motivates it, not as a
   standalone slice.
3. **Registration** — `MeshSourceRegistrar` gains `"prometheus"` in the `usage[].source` valid-values
   list (slice 1 §1.2), reachable as `"usage": [{ "source": "prometheus", "options": { … } }]` with no
   host code change beyond the registrar entry.
4. **Fetch-isolation re-confirmation** (brief §3's explicit ask) — a test proving a slow/unreachable
   Prometheus endpoint degrades only the usage slice of a `FleetView`/`usage.json` run, never stalls
   the aggregator or the composite read model, using the same per-fetch timeout the aggregator already
   applies to every source.
5. **The live-Prometheus CI verification** described under D5 — gates this increment's completion, not
   a separate slice.

Deferred (named, not forgotten):

- **`Benzene.Mesh.Usage.Elasticsearch`** — real, wanted, but needs its own short decision note first:
  which ES metrics schema is it reading? The OTel Collector's `elasticsearchexporter` writing raw
  OTel metric documents, or Elastic's own ECS/APM `metrics-*` data streams (different field names,
  different aggregation shape, different recommended-by-Elastic path)? That's a D2-shaped question
  specific to Elasticsearch and deliberately not resolved by this note — resolving it by guessing
  would be the same mistake the whole brief exists to avoid, one adapter later.
- **Elasticsearch as `IMeshTraceSource`** (Elastic APM) — deferred per D1; no shared template with
  X-Ray/Tempo/Jaeger, not requested yet.
- **`IMeshIssueSource`**, for any backend — deferred per D3; needs its own design note on pull-model
  issue semantics before any implementation, Elasticsearch included.
- **Live-container CI verification of the existing Tempo trace-API and PromQL adapters** — recommended
  under D5 as a follow-up ticket; fixes already-shipped adapters, does not gate this slice.
- **A secret-store URI scheme** (Vault/AWS Secrets Manager/…) beyond the `*EnvVar` convention — no
  current requirement; revisit if a customer asks for it concretely.
