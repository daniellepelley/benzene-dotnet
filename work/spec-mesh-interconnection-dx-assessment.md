# Using the Spec + Mesh Data for Interconnection & Safe Change — DX Assessment

**Status:** Assessment (proposals, not yet approved for implementation)
**Date:** 2026-08-09
**Owners:** dx-champion (build-toolchain half) + mesh-product-owner (mesh-data half); synthesized
**Companion docs:** [`service-mesh-roadmap-1.0.md`](service-mesh-roadmap-1.0.md),
[`benzene-clients-vision.md`](benzene-clients-vision.md),
[`deployment-descriptor-design.md`](deployment-descriptor-design.md),
the shared [Mesh UI product vision](https://github.com/daniellepelley/Benzene/blob/main/work/mesh-ui-product-vision.md)
(spec repo), the cross-language
[code-generation guide](https://github.com/daniellepelley/Benzene/blob/main/docs/guides/code-generation.md),
and the shipped [contract-testing cookbook](../docs/cookbooks/contract-testing.md).
The archived [`archive/dx-roadmap-1.0.md`](archive/dx-roadmap-1.0.md) is the dated first DX audit
this builds on.

---

## 1. The question

Benzene services are self-describing (the derived `spec` operation; the mesh descriptor with its
stable `descriptorHash`) and the estate is observable (mesh artifacts: manifest, per-service specs,
topics/topology/usage, the issue feed). The spec and benzene-dotnet are close to releasable, and the
Mesh UI exists. **But the capabilities this data enables — generating clients, catching breaking
changes, answering "who consumes this topic?" — are not as accessible as they should be.** This
assessment asks: what should we build (or merely package) so that spec data + mesh data actively
drive interconnection between services and make changing services safe — for developers first, and
for business users through the mesh?

The maintainer's three seed ideas, all assessed below: (a) build tools that generate a typed
client — an async method / wrapper class per topic; (b) a build step that extracts the service's
topic + schema definitions to a file as a pipeline artifact feeding codegen/docs; (c) contract
testing in the build pipeline.

## 2. The headline finding

**Almost all of the hard engineering already exists. The dominant gap is plumbing, packaging, and
discoverability — the paved road, not the machinery.** Concretely:

| Capability | Machinery (exists today) | Missing paved road |
|---|---|---|
| Typed client per service | `Benzene.CodeGen.Client/MessageClientSdkBuilder` (typed client + DTOs + baked-in contract `HashCode` + `RequiredTopics` routing check) | CLI can only fetch the contract from a **deployed AWS Lambda**; no file/URL source; no build-step packaging |
| Wrapper class per topic | `AtomicClientSdkBuilder` — exactly the "one async wrapper per topic" idea | **Undocumented anywhere in docs/** |
| Contract artifact on build | `tools/Benzene.Descriptor` — working dotnet tool: loads the built DLL, runs ConfigureServices/Configure network-free, emits the spec + mesh `descriptorHash`; example MSBuild `.targets` included; design settled in [`deployment-descriptor-design.md`](deployment-descriptor-design.md) | **Not in the sln, not packed, not in CI, not documented** |
| Breaking-change detection | `Schema.OpenApi/Compatibility`: `SchemaCompatibilityComparer` + direction-aware rules + `SchemaCompatibility.EnsureBackwardCompatible` (tested; [cookbook](../docs/cookbooks/contract-testing.md) exists) | No CLI verb; baseline production is hand-waved ("get them however your app exposes them"); .NET-test-only, so polyglot CI can't gate |
| Who-consumes-what | `topics.json` consumers/producers per (topic, version); `MeshTopicVersionCompatibility` (produced-not-consumed etc.); usage counts; observed consumers from trace parentage; `OwningTeam` | No one-command answer; evidence spread across UI screens; nothing CI-consumable |
| Contract drift at runtime | Client `HashCode` vs provider schema health check; `AddContractCheck` on the probe-less `contracts` topic; mesh `contractDrift` + `Changes[]`/`RemovedTopics` | Drift detected **after** deploy; run-over-run diffs overwritten (no history); consumer lists never joined to the compat verdict |

Two genuine **data gaps** (everything else is pure tooling over existing artifacts):
1. The usage metric standard (`benzene.messages.processed`, [`docs/mesh-usage-feed.md`](../docs/mesh-usage-feed.md))
   has **no `version` tag**, so CloudWatch/App-Insights planes can never answer "is v1 still carrying
   traffic?" — only the collector plane can, cumulatively. The pipeline already knows `topicVersion`.
2. Observed-consumer edges (trace parentage) live only in the collector's in-memory plane — never
   persisted into the static artifacts that CLI tools and the static-floor UI read.

Neither fix widens the Cloud Service Profile: #1 is an observability metric-standard change
(coordinate with observability-product-owner), #2 mirrors the existing `CollectorUsageSource`
bridge pattern (the spec's mesh.md §9 names it as the natural follow-up).

## 3. The unifying shape: one artifact, one CLI, one loop

The proposals below aren't independent features — they form a single arc:

```
        build time                          CI                            estate
┌────────────────────────┐   ┌───────────────────────────────┐   ┌─────────────────────────┐
│ benzene-descriptor      │   │ benzene diff  (self-gate)     │   │ mesh artifacts           │
│ --emit spec|descriptor  │──▶│ benzene compat-check (vs mesh │◀──│ services/{name}.json     │
│ → MyService.spec.json   │   │   baseline + consumers+usage) │   │ topics/topology/usage    │
│ → MyService.service.json│   │ benzene impact (topic query)  │   │ changelog.json (new)     │
└──────────┬─────────────┘   └───────────────────────────────┘   └───────────┬─────────────┘
           │                                                                  │
           ▼                                                                  ▼
┌────────────────────────┐                                       ┌─────────────────────────┐
│ benzene build --file/   │                                       │ Mesh UI topic drill-in   │
│   --mesh (typed client/ │◀──────────────────────────────────────│ "Consume this topic":    │
│   topic-client) or      │        copy CLI line / schema         │ schema + specUrl + CLI   │
│   MSBuild ContractItem  │                                       │ line + AsyncAPI link     │
└────────────────────────┘                                       └─────────────────────────┘
```

- **The artifact** (`{Service}.spec.json` + `{Service}.service.json`) is the currency: produced by
  the descriptor tool at build, consumed by codegen, diffed by the gate, published to the mesh store.
- **The CLI** (`benzene`, which already ships as a dotnet tool) grows verbs over that currency:
  today `build`/`spec`/`profile-check`; add `diff`, `compat-check`, `impact`, and file/URL/mesh
  sources.
- **The loop** closes in the Mesh UI: browse a topic → grab its schema/spec URL/ready-made CLI line
  → generate the client → the generated client's baked-in hash feeds the runtime drift check → the
  mesh reports drift → back to the UI.

This also matches the cross-language code-generation guide: the inputs are spec-defined wire
artifacts, so every port (and any CI, in any language) consumes them identically.

## 4. The opportunities, ranked

Merged ranking across both halves. Effort S/M/L; "PKG/DOC" = the code already does this.

### 1. Ship the contract-artifact build step — promote `tools/Benzene.Descriptor` (S–M, mostly PKG)
The seed idea (b), already built as a spike: a dotnet tool that loads the built service DLL in an
`AssemblyLoadContext`, constructs the app network-free (verified across Lambda/worker/ASP.NET
hosting in [`deployment-descriptor-design.md`](deployment-descriptor-design.md)), and emits the
descriptor. Productize: add to the sln (**needs owner approval** — its README forbids sln
restructuring without it), pack as `benzene-descriptor`, add `--emit spec|descriptor|both` so one
invocation writes both the codegen/compat-gate input (`spec.json`) and the mesh/infra shape
(`service.json`), ship the opt-in MSBuild `.targets` (`<BenzeneEmitDescriptor>true</>`), wire into
CI as an uploaded artifact. **This unlocks every other item.** Risks: tool/service `Benzene.*`
version pinning across the ALC (fail loudly on mismatch); services whose ConfigureServices does I/O
(clear error, not a hang).

### 2. CI-safe CLI + `benzene diff` — the build-pipeline contract gate (S)
Seed idea (c), 100 lines from done: a `diff` verb over the existing `SchemaCompatibilityComparer` —
`benzene diff --baseline old.spec.json --current new.spec.json --fail-on breaking` — printing the
report and exiting non-zero on breaking changes. Plus CLI hygiene that currently blocks *any* CI
use: `ClientCodeBuilder.Build` swallows exceptions and exits 0; `AwsLambdaSpecClient` returns null
and NREs. Two documented recipes: producer self-gate (regenerate spec at build, diff vs committed
baseline; `descriptorHash` as the cheap no-change fast path) and consumer gate (pinned spec vs
producer's latest artifact). Rewrite the [contract-testing cookbook](../docs/cookbooks/contract-testing.md)'s
hand-waved step 1 around the descriptor tool. Hash-multiplicity risk:
`CodeGenHelpers.GenerateHash`(=`MeshHashing`) vs `MeshDescriptorHashing` are different values — the
docs must name which is which; gates should use the structural comparer, hashes only for change
detection.

### 3. `benzene compat-check` + `benzene impact` — mesh-joined change safety (M)
The step beyond a pairwise diff, and the strongest adoption story in this assessment ("the platform
tells you who you break before you ship" — no mainstream competitor does consumer-joined
breaking-change analysis from *derived* contracts):
- **`compat-check --spec candidate.spec.json --mesh <manifest-url>`**: fetch the mesh's stored
  baseline (`services/{name}.json` → `specJson`), run the comparer, and for each breaking change
  name the declared consumers from `topics.json` + usage counts: *"breaking: `order:create` v1
  request field `sku` removed — breaks `payments-api`, `shipping-api`; v1 carried 8.4k msgs in the
  window."* Exit codes for CI.
- **`impact --topic <id>[@version] --mesh <manifest-url> [--fleet <url>]`**: one command printing
  declared + observed consumers, per-version traffic (window/source-labelled, reusing the UI's
  honesty rules verbatim), produced-not-consumed/consumed-not-produced, owning teams, open
  annotations. `--fail-on-consumers` for CI retirement gates.
Caveats to preserve in output: declared ≠ actual consumers (an upcaster may bridge — prompt to
confirm, don't verdict); baseline freshness (`snapshotAtUtc`, warn when stale); for non-.NET
services the field-level comparer degrades to the JSON-level `Changes[]` diff — say so. This closes
[`service-mesh-roadmap-1.0.md`](service-mesh-roadmap-1.0.md)'s own deferred "CI/dev-time surfacing
of the compatibility gate" item (§10.8).

### 4. One-liner client generation (S then M)
Seed idea (a), two tiers:
- **Tier 1 (S):** `--file <spec.json>` / `--url` / `--mesh <manifest-url> --service X [--topic Y]`
  sources for `benzene build` and `benzene spec` (plumbing exists: `EventServiceDocumentDeserializer`,
  the HTTP spec probe in `profile-check`). Decouples generation from deployed-Lambda + AWS creds.
- **Tier 2 (M):** an MSBuild package so integration really is one line:
  `<BenzeneServiceContract Include="contracts/orders.spec.json" Mode="topic-client"/>` → generated
  into `obj/`, added to `@(Compile)`, incremental. A Roslyn source-generator variant is deferred:
  the builders depend on `Microsoft.OpenApi`/net10.0 and generators must be netstandard2.0 — port
  later without changing the artifact.
- **DOC (free):** `topic-client` / `AtomicClientSdkBuilder` — the literal "wrapper class per topic"
  — exists today and appears nowhere in docs. Same for `MessageHandlerBuilder` (scaffold handler
  stubs *from* a contract — the consumer-first workflow).

### 5. Browse-and-generate — close the loop in the Mesh UI (S, mesh side)
The browse experience and the generate experience never meet today: a developer on the topic
drill-in has the full inlined schema on screen and no path to a typed client. Fix without any spec
change: (i) document the mesh artifact store as the official codegen input contract
(`manifest.json` → `services/{name}.json` → `specJson` — the mesh **is** the schema registry);
(ii) a "Consume this topic" panel on the drill-in — copy/download schema JSON, copy `specUrl`, copy
the ready-made `benzene build --mesh … --service … --topic …` line, per-service AsyncAPI link
(static-floor-safe: links + clipboard only); (iii) optional per-service `asyncapi/{service}.json`
slices (the compositor already namespaces per service). UI affordances coordinate with the shared
[Mesh UI product vision](https://github.com/daniellepelley/Benzene/blob/main/work/mesh-ui-product-vision.md)
(spec repo owns the shared UI). **Rejected on tautness grounds:** a per-topic schema endpoint on
the Cloud Service Profile — the aggregator already answers centrally what that would ask every
service to answer individually.

### 6. The time dimension for business users — `changelog.json` + windowed usage (M)
"What changed this week?" is unanswerable: run-over-run diffs (`Changes[]`/`RemovedTopics`) are
computed then overwritten. Aggregator appends dated, non-empty diffs to a bounded rolling
`changelog.json` in the artifact store (same non-regenerable-artifact discipline as annotations);
Mesh UI gains a "Changes" view with a since-picker. Pair with the already-filed windowable-usage
requirement (prefer bucketed usage entries on the artifact path, keeping the static floor
first-class). Also strengthens #3 (cite when a change landed).

### 7. Data foundations (S each, coordinate)
- **`version` tag on `benzene.messages.processed`** (observability-product-owner): the single datum
  turning "retire v1" from folklore into evidence on every backend. Bounded cardinality (few live
  versions per topic). Metric-standard change ([`docs/mesh-usage-feed.md`](../docs/mesh-usage-feed.md)),
  not a profile change.
- **Collector→artifact observed-consumers bridge** (an `IMeshConsumerSource` mirroring
  `CollectorUsageSource`): gives the static floor — and the #3 CLI — observed-consumer evidence
  with no live endpoint.

### 8. Docs pass (S, all DOC)
The `benzene` CLI ships today and is a named-in-a-table ghost: no install command, no command
reference, no example invocation anywhere. Document: `dotnet tool install`, every verb, the
artifact conventions, `topic-client`, `MessageHandlerBuilder`, and the two CI recipes. Highest
leverage-per-hour item in this assessment.

## 5. What NOT to invest in

- **`Benzene.CodeGen.Terraform`** — unpacked, and superseded by the descriptor-first
  reference-generator posture in [`deployment-descriptor-design.md`](deployment-descriptor-design.md).
  Leave.
- **A per-topic schema endpoint on the Cloud Service Profile** — tautness wins; the mesh answers it.
- **A Pact-style parallel contract-testing system** — reuse the existing hashing/manifest/comparer
  plumbing (the ruling the [contract-testing cookbook](../docs/cookbooks/contract-testing.md)
  already embodies).
- **Porting the codegen builders into a Roslyn source generator now** — L-effort netstandard2.0/
  Microsoft.OpenApi port for the same artifact; the MSBuild-exec shape delivers first.
- **Dashboards/general metrics views** — out of the mesh's deliberate non-compete lane.

## 6. Sequencing and the release tie-in

Suggested order: **1 → 2 → 4(tier 1) → 8** land as a coherent "contract tooling" release story
(each S or S–M, and 8 is free); then **3** (the differentiator, needs 1's artifact + mesh baseline);
then **4(tier 2), 5, 6, 7** as follow-ons. Items 1+2+4+8 together turn the three seed ideas into
shipped, documented workflows almost entirely by *packaging code that already exists* — which is
precisely the accessibility gap this assessment was asked about.

Estate-level follow-on (sketched in `deployment-descriptor-design.md`, sequence after the above):
per-project descriptor emit + the existing `MeshAggregator` reconciliation = whole-system topology
computed at build time, before anything deploys.

## 7. Open questions for the maintainer

1. Approve promoting `tools/Benzene.Descriptor` into `Benzene.sln`/`src` (its README requires
   explicit owner approval for sln changes)?
2. Where do published contract artifacts live between services — repo files, an S3/Blob bucket
   (reusing `Benzene.Mesh.Aws.S3`/`Azure.Blob` publishers), or "the mesh store is the registry"
   (this assessment's lean)?
3. Should `compat-check` breaking-changes **fail** CI by default, or warn-only until the estate has
   baselines established ([`service-mesh-roadmap-1.0.md`](service-mesh-roadmap-1.0.md) §4.5's open
   question — this assessment leans fail-on-breaking with an explicit `--warn-only` opt-out)?
4. Green-light the `version` metric tag with observability-product-owner?

## Provenance

Synthesized 2026-08-09 from two read-only agent assessments: dx-champion (build toolchain:
CodeGen.* packages, CLI commands and failure modes, Schema.OpenApi spec derivation + compatibility,
tools/Benzene.Descriptor, docs coverage) and mesh-product-owner (Mesh.* packages, the spec repo's
mesh.md / cloud-service-profile.md contracts, UI capabilities, usage/topology/issue planes,
living-doc cross-check). Every "exists today" claim was verified against source by the reporting
agent; nothing in this document has been execution-tested (no builds/runs were performed). Note:
the agents read the pre-2026-08-09 repo layout (before the work/ one-home reorg and the
`Client.Http → Clients.Http` rename); file references here have been updated to the new layout,
but package-internal claims predate those commits.
