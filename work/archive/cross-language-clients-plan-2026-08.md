> ARCHIVED 2026-08-20: actioned; benzene-dotnet Phase 2 shipped (`src/Benzene.HealthChecks.Schema` contract-hash loop); cross-repo phases live with their owning repos.

# Cross-Language Generated Clients — Implementation Plan

**Status:** **Phases 1 and 2 are DONE** (2026-08-20): the contract document is promoted in the
specification repo and .NET adopts it as the reference implementation. **Phases 3-7 remain open**, and
they are work for the *other* language repos — `benzene-typescript`, `benzene-python`, `benzene-go` —
not for this one; nothing further is dispatchable from `benzene-dotnet`. (Original status: planned,
ready to dispatch; owner product decision made 2026-08-13, architecture recommendation below adopted by
this plan.)
**Date:** 2026-08-13
**Owner direction (the product decision, already made):** client generation extends to
**TypeScript, Python, and Go** — a Node/Python/Go consumer of any Benzene service gets the same
typed, topic-scoped, drift-hashed client from the same committed `{Service}.spec.json` that the
.NET tooling consumes today. The contract artifact becomes the ecosystem's lingua franca.
**Audience:** implementation agents. Each phase is a self-contained task. Do Phase 1 first, then
Phase 2; Phases 3–5 are independent of each other after Phase 2.
**Companion docs:** [`spec-mesh-tooling-implementation-plan-2026-08.md`](spec-mesh-tooling-implementation-plan-2026-08.md)
(the .NET half this plan generalizes — Phases 3/3b/6 and the 2026-08-13 dogfooding findings),
[`benzene-clients-vision.md`](../benzene-clients-vision.md) §2.5 (generated clients as the primary
developer-facing surface).

**Decisions already made (do not re-litigate):**
1. **TS, Python, Go, in that order** — the owner listed them in that order; the phases follow it.
2. **The committed `.spec.json` is the input everywhere.** Same artifact the .NET `benzene build
   -file …` path consumes; produced by `Benzene.Descriptor` on the producer's build. No new
   artifact format, no per-language contract dialects.
3. **Generated clients cover DOMAIN topics only — never `benzene:*` reserved endpoints** (owner
   ruling, benzene-dotnet commit `d280caa`). No generated health check, no reserved topic in any
   required-topics output, in any language. Downstream health checking is a library concern
   (`dc458ec`), not a codegen concern — ports may mirror that later; it is not part of this plan.
4. **`decimal`→`number`→double fidelity is PARKED** (dogfooding finding 7d): *"keep it to whatever
   is in the schema definition, because that's what we're governed by."* Every language's type
   mapper maps what the schema says. Do not add `format` heuristics, do not reopen.
5. **Contract-drift CI is PARKED** (dogfooding finding 7e). The hash ships in every generated
   client (it's the enabler); the CI/mesh drift feature that consumes it cross-repo is a separate
   future effort.
6. **This plan reverses Amendment C's cross-language deferral** — see Phase 1.

---

## The generator-architecture decision

The central design choice: where do the TS/Python/Go generators live and what runs them?

| | (a) One polyglot generator in the .NET `benzene` CLI | (b) A generator per port repo, in the port's own language | (c) Hybrid: shared spec + fixtures, per-port generators |
|---|---|---|---|
| Parsers of the contract document | 1 | N (4) | N (4) |
| Consumer toolchain | **Needs the .NET SDK to consume a TS/Go service** — a Node shop generating a client for a Python service must install .NET | `npx` / `pipx` / `go run` — native to each ecosystem | same as (b) |
| Generated-code ↔ runtime coupling | Generator releases with benzene-dotnet; every port-library API change (sender signature, result type) needs a cross-repo .NET release | Generator ships **with the library whose API it emits calls into** — versioned together, tested together | same as (b) |
| Idiomatic output quality | C# `LineWriter` templates guessing at idiomatic TS/Python/Go, reviewed far from the people who know the idiom | Written and reviewed where the idiom lives | same as (b) |
| Honesty across N implementations | Free (there's one) | **Conformance fixtures — the mechanism that exists for exactly this** | fixtures + a spec'd generation-semantics contract |
| Owner precedent | Against — implementations live in their own repos | Matches the repo split and the porting guide's "spec-first, idiomatic, never translate the C# API" | Matches |

**Recommendation — (c), which is (b) with the honesty mechanism made explicit.** Each port repo
gets a generator in its own language, distributed the way that ecosystem distributes dev tools.
What keeps four parsers from drifting is the same thing that keeps four envelope implementations
from drifting: the contract-document format, the topic-scoping semantics, and the contract-hash
algorithm get **promoted to the spec repo with conformance fixtures**, and every port (including
.NET, which becomes the reference implementation) runs the same fixtures through its own runner.

The decisive argument is the sender abstraction: generated code's only dependency is the port's
transport-agnostic sender (`IBenzeneMessageSender` in .NET). The generator must know that API
intimately and must move when it moves — it belongs in the repo that owns it. The cost — N parsers
— is real, but the contract document is small (one bespoke top level + OpenAPI 3.0 schema
objects), and "N implementations of one pinned contract, kept honest by shared fixtures" is this
project's established, working pattern, not a new risk.

Rejected: (a) outright — requiring the .NET SDK to consume a non-.NET service defeats the
strategic goal ("a Node consumer of any Benzene service"), and a C# code generator emitting
idiomatic Go is a maintenance liability nobody signed up for.

---

## §0. Ground rules for every phase — READ FIRST

**Repos.**
- **Benzene** (cross-language spec repo, `/home/user/Benzene`): Phase 1 only. CRITICAL rule from
  its `AGENTS.md`: a change to an observable contract is a **spec change** — made in
  `docs/specification/**` with conformance fixtures updated, after which **every language port
  re-vendors and re-verifies**. Never change an existing fixture to match one implementation's
  quirk. Phase 1 *adds* fixtures (a deliberate spec change); it does not edit existing ones.
  Spec-repo commits go on the **designated session branch** (given at dispatch — do not commit to
  main).
- **benzene-dotnet**: Phase 2. Conventions per `spec-mesh-tooling-implementation-plan-2026-08.md` §0
  (sln registration, single test project, no FluentAssertions, per-package `CLAUDE.md`).
- **benzene-typescript / benzene-python / benzene-go**: Phases 3–5. **These repos were NOT
  readable when this plan was written.** Every statement about them below is an instruction to
  verify, not a fact. Each language phase opens with a mandatory audit step; follow each repo's
  own `AGENTS.md`/`CLAUDE.md` conventions over anything generic said here.

**Verify before you edit.** benzene-dotnet paths and APIs cited here were verified 2026-08-13
(`src/Benzene.CodeGen.Client/{MessageClientSdkBuilder,AtomicClientSdkBuilder,ClientSdkOptions,TopicScope,OpenApiSchemaCSharpTypeBuilder}.cs`,
`src/Benzene.CodeGen.Core/CodeGenHelpers.cs`, `src/Benzene.CodeGen.Cli.Core/Commands/Build/CodeBuilderFactory.cs`,
`src/Benzene.CodeGen.Build/build/Benzene.CodeGen.Build.targets`,
`src/Benzene.Schema.OpenApi/EventService/*`). The repo moves fast; re-read before changing.

**Fixture vendoring chain (the established mechanism Phases 2–5 plug into).** The spec repo owns
`docs/specification/conformance/*.json`; benzene-dotnet carries a vendored snapshot under
`test/conformance-fixtures/` plus a `SPEC_VERSION` marker, and
`.github/workflows/conformance-drift-check.yml` diffs the snapshot against canonical on every
push/PR/weekly. Each port phase must find (or, if absent, replicate) the same chain in its repo —
vendor the new fixtures, run them in the port's own conformance runner, keep the drift check
honest.

---

## The parity checklist

The .NET feature set is the definition of "the same client" in every language. Every language
generator must cover each row — or record explicitly, in its docs, why a row doesn't map and what
the idiomatic equivalent is. This table is referenced by Phases 3–5; Phase 1 pins rows 2–6 in the
spec so they are contract, not convention.

| # | Feature | .NET reference | Cross-language requirement |
|---|---|---|---|
| 1 | Input: committed `.spec.json` file | `benzene build -file …` (`FileSpecSource`) | File input is mandatory. `--url`/`--mesh` sources are optional, add-if-cheap. |
| 2 | Shape: **service client** — one client per service, one method per request topic | `MessageClientSdkBuilder` (`-output client`) | Required. Class/struct/object per the language's idiom; typed request/response per method. |
| 3 | Shape: **topic-client** — one self-contained client per topic, with only that topic's reachable schema closure | `AtomicClientSdkBuilder` (`-output topic-client`); `ReachableSchemas` walks `$ref`/items/additionalProperties/properties/allOf-anyOf-oneOf, cycle-safe | Required, including the schema-closure semantics (pinned by fixture, Phase 1). Per-topic hash falls out topic-scoped. |
| 4 | **Topic include-list** to minimise coupling surface | `ClientSdkOptions.Topics` / `-topics a,b`; unknown topic → fail loud listing the document's actual topics | Required, including the fail-loud rule (pinned by fixture). |
| 5 | **Reserved `benzene:*` excluded** in both shapes by default; explicit opt-in only (`IncludeReservedTopics` or naming one in the include-list) | `TopicScope.Apply` + `RequestResponse.Reserved`; the domain-only ruling `d280caa` | Required (pinned by fixture). Detection is the document's `reserved: true` flag **plus** the `benzene:` prefix rule (a document from an older producer may lack the flag). |
| 6 | **Contract hash** embedded in the generated client | `HashCode` property, `CodeGenHelpers.GenerateHash` | Required, per the spec-pinned algorithm (Phase 1 §hash — today's .NET algorithm is NOT portable; see Phase 1). |
| 7 | Namespace / module configuration, used exactly | `ClientSdkOptions.Namespace`, `-namespace` | Required, translated: C# namespace → TS output directory + export structure; Python package/module name; Go package name. "Used exactly, no magic suffix" carries over. |
| 8 | **RequiredTopics** for startup route validation | `[OutboundRoutingContract]` static class; reflected over by `ValidateOutboundRouting()` | **Conditional on the port** (audit step): emit the topic list as an exported constant always (it's free and self-documenting); wire it into startup validation only where the port has a declarative outbound-routing seam. Porting-guide §4 already records that imperatively-wired ports (Go, Python) fail loudly at construction and may not need a separate pass — that's an acceptable, documented answer to this row. |
| 9 | Registration / DI equivalent | `Add{Service}ServiceClient(this IBenzeneServiceContainer)`, scoped (`dc458ec`); atomic mode also emits an aggregate `Add{Service}Clients()` | **Idiomatic equivalent, not a translation.** Where the port has a container abstraction → generate the registration against it. Where it doesn't (Go has no DI-container convention; Python typically doesn't) → the idiomatic equivalent is a generated constructor/factory (`NewOrdersClient(sender)`, `create_orders_client(sender)`) and that row is DONE — do not invent a container. |
| 10 | Transport-agnostic output | Generated code depends only on `IBenzeneMessageSender` + `IBenzeneResult`; zero transport references (verified) | Required: output depends only on the port's sender abstraction + result type. Transport binding stays out-of-band (the port's outbound routing / client wiring). |
| 11 | Type mapping | `OpenApiSchemaCSharpTypeBuilder` + `CSharpTypeName` (incl. allOf inheritance, oneOf-with-shared-base, discriminator polymorphism, additionalProperties → dictionary) | Map schema → TS interfaces / Python dataclasses-or-pydantic (pick what the port already uses — audit) / Go structs. Inherit the "schema definition governs" stance (decision 4). Composition rows: allOf → extends/embedding, discriminator → the language's tagged-union idiom (TS discriminated unions are *better* than the C# attribute encoding — take the win), oneOf-no-base → `unknown`/`Any`/`any` honestly. |
| 12 | Method naming from topic | `TopicReversedMethodName` default; pluggable `IMethodName` | Per-language casing/naming idiom. **Naming is API shape and explicitly NOT conformance** (conformance README's existing rule) — no fixture pins it. |
| 13 | Fail-loud CLI | Real exit codes; unknown `-output` lists valid values | Required: non-zero on unknown mode, unknown topic, unparseable document. |

Flag spelling is idiom, not contract: the .NET CLI's single-dash `-file`/`-topics` is a .NET-ism;
each port uses its ecosystem's flag conventions for the same semantics.

---

## Phase 1 — Promote the contract document to the spec repo *(Benzene repo)*

**Goal:** the `.spec.json` format, its generation semantics, and a language-neutral contract-hash
algorithm become spec, with conformance fixtures — so four generators can parse one truth.
**Depends on:** nothing. **Effort:** M. *Unlocks everything else.*

**This supersedes Amendment C's deferral.** `spec-mesh-tooling-implementation-plan-2026-08.md`'s 2026-08-12
Amendment C recorded: *"the spec document (`EventServiceDocument`) is deliberately .NET-side and is
not being promoted to the spec repo in this round."* Cross-language generation reverses that by
necessity — a Go generator cannot be built against a .NET-private format. Record the supersession
in that plan file (one line in Amendment C pointing here) as part of this phase.

Steps:

1. **New `docs/specification/contract-document.md`** ("Contract Document" — the `.spec.json`).
   Derive the normative shape from the .NET serializer
   (`src/Benzene.Schema.OpenApi/EventService/EventServiceDocument.cs`, `RequestResponse.cs`,
   `Event.cs`, `HttpMapping.cs` in benzene-dotnet) and the committed real-world instance
   (`examples/AwsMesh/Orders/contracts/payments.spec.json`). Pin:
   - Top level: `openapi: "3.0.1"` (heritage marker), `info` (title = service name, version),
     optional `messageEndpoint`, optional `transports[]`, `requests[]`, `events[]`, `components`
     (OpenAPI 3.0 schema objects; `$ref` only into `#/components/schemas/…`).
   - `requests[]`: `topic`, optional `version` (absent ≠ empty — absent means unversioned),
     optional `reserved: true`, optional `httpMappings[]` (`method`, `path`), `request`/`response`
     schema-or-ref, optional `example` (informative decoration, see hash).
   - `events[]`: `topic`, optional `version`, `message` schema, optional `example`.
   - Provenance: this is the document R5 of the Cloud Service Profile requires a service to derive
     and serve at `/benzene/spec` (`?type=benzene&format=json`) — cross-link both ways
     (`cloud-service-profile.md` R5 gains one sentence + link; keep the edit minimal).
   - **Generation semantics section** (what makes a conforming client generator): domain-only rule
     (reserved excluded by default, `reserved` flag OR `benzene:` prefix), include-list semantics
     with the fail-loud unknown-topic rule, topic-scoped schema closure (the `ReachableSchemas`
     walk, specified language-neutrally), and the rule that generated output depends only on the
     port's transport-agnostic sender. Method naming and file layout are explicitly API shape /
     idiom — out of conformance scope.
2. **§ Contract hash — specify it language-neutrally, because today's is not.** Finding, verified
   2026-08-13: `CodeGenHelpers.GenerateHash(EventServiceDocument)`
   (`src/Benzene.CodeGen.Core/CodeGenHelpers.cs`) computes lowercase-hex **HMAC-SHA256 with an
   empty key** over the **Microsoft.OpenApi-serialized JSON** of a normalized document (examples,
   `messageEndpoint`, `transports`, `reserved` stripped). The byte stream is a .NET-serializer
   artifact — no other language can reproduce it without cloning that serializer. `mesh.md` §9
   already relegates the sibling `MeshHashing` (HMAC over raw spec text) to ".NET-internal" and
   §2.2 pins the wire `descriptorHash` as canonical-JSON SHA-256. Follow that precedent:
   - `contractHash = "sha256:" + lowercase-hex(sha256(canonicalJSON(normalize(document))))`.
   - `normalize`: remove every `example`, `messageEndpoint`, `transports`, and all `reserved:
     true` **entries' flags AND (for the published whole-service hash) the reserved entries
     themselves** — the published hash of a service covers its **domain projection**, consistent
     with the domain-only ruling. A hash is a pure function of any document projection; comparing
     hashes is meaningful only between identical projections (say so explicitly — a topic-scoped
     client's hash is comparable to the same topic-scoped projection, not to the service hash).
   - `canonicalJSON`: **RFC 8785 (JCS)**. Divergence from mesh §2.2's documented-member-order
     canonicalization is deliberate and should be recorded in the doc: `descriptorHash` is
     *per-port by design and never compared across ports* (mesh §2.2's own words), so a documented
     order sufficed; `contractHash` is compared **across** ports — its canonicalization must be
     mechanical, and JCS has off-the-shelf implementations in all four languages (.NET, `canonicalize`
     on npm, `rfc8785` on PyPI, `gowebpki/jcs` for Go). Schema `components` are producer-defined
     arbitrary JSON, where "declaration order" is undefined anyway.
3. **Conformance fixtures** (new files; never touch existing ones):
   - `conformance/contract-document-cases.json` — parse/validate cases (minimal valid document;
     versioned and reserved entries; unknown-topic include-list → error), **topic-scope projection
     cases** (given document + options {topics, includeReserved} → expected surviving topic set),
     and **schema-closure cases** (given document + topic → expected component-key set; include a
     `$ref` cycle and a oneOf/allOf reach case).
   - `conformance/contract-hash-cases.json` — document → exact expected `contractHash` string, for:
     the minimal document, one with examples/messageEndpoint/transports present (proving
     normalization), one with reserved entries (proving the domain projection), and one
     topic-scoped projection. Compute expected values with an independent JCS implementation, not
     the .NET serializer.
   - Extend `conformance/README.md`: fixture-table rows, a "client-generation conformance" claim
     row (required only for ports that ship a generator — like the mesh fixtures' conditionality),
     and the case formats.
4. **Wire into the spec's front doors:** `docs/specification/index.md` gains the doc under Core;
   `porting-guide.md` gains a short "client generation" note pointing at the doc + fixtures (its
   §1 table already names codegen as a registration idiom — don't restructure it).
5. **Website check:** from the Benzene repo root,
   `dotnet run --project website/generator -- --out website/dist` — the broken-link self-check must
   pass with the new pages linked.

**Acceptance:** spec doc + two new fixture files exist and are linked from `index.md` and the
conformance README; expected hashes independently computed; website generator runs clean; no
existing fixture modified; Amendment C cross-referenced; all commits on the designated branch.

---

## Phase 2 — .NET adopts the spec as reference implementation *(benzene-dotnet)*

**Goal:** the existing generator becomes the reference implementation of Phase 1's contract —
proving the spec is implementable before three ports copy it — and the .NET hash migrates to the
portable algorithm.
**Depends on:** Phase 1. **Effort:** S–M.

Steps:

1. **Re-vendor fixtures**: copy the two new canonical files into `test/conformance-fixtures/`,
   bump `test/conformance-fixtures/SPEC_VERSION` (the drift-check workflow requires the snapshot
   byte-identical to canonical — note it diffs against the spec repo's main; until Phase 1 merges,
   land this behind the spec merge or point the local run at the branch).
2. **Conformance runner** (`test/Benzene.Conformance.Test/`): a `ContractDocumentConformanceTest`
   running the parse/scope/closure cases through `EventServiceDocumentDeserializer` +
   `TopicScope.Apply` + `AtomicClientSdkBuilder`'s closure, and a `ContractHashConformanceTest`
   for the hash cases. (`TopicScope` is `internal` — use `InternalsVisibleTo` or test through the
   builders, whichever the existing test project already does for internals.)
3. **Migrate the generated-client hash** to spec `contractHash`: implement it beside
   `CodeGenHelpers.GenerateHash` (JCS canonicalization over the normalized document — new code, do
   not bend the OpenApi serializer), and switch `MessageClientSdkBuilder.AddHashCode` to it.
   **Verify and fix the projection-comparability gap while there**: since `d280caa`, a service
   client hashes the *domain-scoped* document, while the provider side
   (`Benzene.HealthChecks.Schema/SchemaHealthCheck.cs`, `GenerateHash(GetAllHandlers())`) hashes
   *all* handlers including reserved — the two can no longer match, so
   `ServiceHealthCheckClient`/`ClientHealthCheckProcessor` drift comparison is currently
   fabricating mismatches. Align the provider side to the spec's domain projection. Add a test
   pinning client-hash == provider-hash for the same service.
4. **Flag the value change honestly**: every newly generated client and every provider's served
   hash changes value once. `CHANGELOG.md` entry + a note in `docs/client-sdks.md`: regenerate
   clients and redeploy providers together, or expect one drift *warning* (it is a Warning, not a
   failure) in mixed fleets. Keep `MeshHashing` untouched (mesh drift is raw-text hashing by
   design, per mesh.md §9).
5. **Regenerate the dogfood consumers** (`examples/AwsMesh/Orders` generated payments client, the
   `examples/CodeGen` consumers) by building; commit any changed generated output the examples
   check in.

**Acceptance:** sln builds; conformance tests green against the vendored fixtures; drift-check
workflow logic passes locally; client hash and provider hash agree for one example service and
both carry the `sha256:` prefix; golden-file tests updated deliberately (hash lines only).

---

## Phases 3–5 — the language generators *(benzene-typescript, benzene-python, benzene-go)*

One phase per port, same template, **in owner order: TS (Phase 3), Python (Phase 4), Go (Phase
5)**. Each is a self-contained agent task against a repo this plan could not read — hence step 0.
**Depends on:** Phase 1 (spec + fixtures); Phase 2 recommended first (a reference implementation
to compare against). **Effort:** M–L each.

**Step 0 — port audit (mandatory, do first, write findings into the port's work notes):**
- Does the port have a **transport-agnostic sender abstraction** (the `IBenzeneMessageSender`
  equivalent: send `(topic, payload, headers?) → result<TResponse>` with transport bound
  elsewhere)? The porting guide's §2 order puts "outbound client + decorators" at step 4, so it
  should exist — verify its exact shape. **If absent, that port's generator phase gains a
  prerequisite task** (build the sender seam first, as its own dispatched slice) — do not
  generate clients against transport-specific clients.
- Does it have a **result type** and a **reserved-topic (`benzene:`) constant**?
- Does it have **outbound routing with startup validation** (drives parity row 8: constant-only
  vs wired)?
- What is its **schema/type story** (does it already depend on a JSON-schema or pydantic/zod-like
  library? follow it, don't import a new worldview)?
- What is its **fixture vendoring + drift-check** setup (mirror benzene-dotnet's
  `conformance-drift-check.yml` if missing)?
- What are its packaging/naming conventions for tools (npm bin name / console-script / cmd
  package)?

**Steps (all three ports):**
1. Vendor the two new fixtures; write the port's conformance runner for the parse/scope/closure
   and hash cases (this forces the parser and hash to exist before any code is emitted — the
   honest-N-parsers mechanism doing its job).
2. Generator package in the port repo: parser → topic scope → schema closure → emitters for the
   **two shapes** → hash constant → registration-or-factory per the audit. Cover every parity
   checklist row or record the documented idiomatic answer.
3. CLI entry point, distributed idiomatically:
   - TS: a `bin` in the port's tooling package → `npx @<scope>/benzene-codegen build --file
     payments.spec.json --output topic-client --module payments --topics payments:capture`
     (scope/name per the repo's existing npm naming).
   - Python: console script (`benzene-codegen …`) via the port's packaging; runnable with `pipx`.
   - Go: a `cmd/benzene-codegen` main; runnable via `go run <module>/cmd/benzene-codegen@latest`.
4. Type emission per parity row 11, with the port's existing serialization idiom (TS interfaces +
   discriminated unions; Python dataclasses **or** pydantic — whichever the port already uses;
   Go structs + json tags, omitempty matching the port's marshaling rules).
5. Tests in the port's own style: golden/snapshot outputs for a fixture document; the generated
   output must **compile/typecheck** in-test (tsc / mypy-or-import / go build).
6. Docs page in the port repo (its docs feed the website via the multi-source generator — follow
   the port's nav conventions so the site build stays link-clean).

**Acceptance (each port):** conformance runner green on the vendored fixtures; generating from a
fixture document yields both shapes, compiling clean, domain-topics-only, include-list honored
with fail-loud unknown topics; the emitted hash equals the fixture's expected value; CLI exits
non-zero on bad input; docs published.

---

## Phase 6 — Build-integration equivalents *(thin; one slice per port, inside each port repo)*

**Goal:** the .NET one-liner (`<BenzeneServiceContract Include="contracts/orders.spec.json"/>` in
`Benzene.CodeGen.Build`) gets an ecosystem-native equivalent — not an MSBuild clone.
**Depends on:** the corresponding language phase. **Effort:** S per port.

- **TS:** a documented npm-script pattern — `"prebuild": "benzene-codegen build --file … --out
  src/generated/…"` (or the repo's build tool's hook). Generated output gitignored, regenerated on
  build, mirroring MSBuild's IntermediateOutputPath stance.
- **Python:** no universal build hook exists — be honest about it. Document the pattern: a
  `benzene-codegen` invocation in the project's task runner (make/nox/hatch script) + a CI check
  that regeneration is clean. Committed generated output is acceptable here.
- **Go:** `//go:generate benzene-codegen build -file contracts/payments.spec.json …` with
  **committed** generated code — the Go convention (and what `go generate` assumes). The CI check
  is `go generate ./... && git diff --exit-code`.
- Each lands as a section in the port's codegen docs page plus wiring in the Phase 7 example —
  no new packages unless the port's conventions demand one.

---

## Phase 7 — Dogfooding: one example consumer per language

**Goal:** proof, per language, of the strategic claim — a consumer gets a typed, topic-scoped,
drift-hashed client from a real committed contract.
**Depends on:** Phases 3–6 per language. **Effort:** S per port.

1. The established fixture is the AwsMesh payments contract:
   `examples/AwsMesh/Orders/contracts/payments.spec.json` in benzene-dotnet (a real
   `Benzene.Descriptor`-emitted document; the .NET Orders example already consumes it — commit
   `8e00d09`). Each port's example **commits its own copy** — that *is* the consumer workflow
   (the producer publishes, the consumer commits a snapshot).
2. Per port: an example consumer that generates a `topic-client` for `payments:capture` **only**
   (`--topics payments:capture` — demonstrating the coupling-surface story), wires the port's
   sender via a fake/in-process transport, and asserts in a test that calling
   `capture(…)`/`Capture(…)` sends topic `payments:capture` with the typed payload, and that the
   embedded hash equals the expected value for that projection.
3. Verify the domain-only rule end to end: the committed payments document contains reserved
   entries (`benzene:spec` et al., `reserved: true`) — assert none appear in generated output.
4. Stretch, not gating: a live cross-language interop check (port consumer → running .NET
   payments service over the envelope endpoint) belongs to the porting guide's "interop form
   (future)" work, not this plan.

**Acceptance:** each port repo has a building, tested example consumer from the committed payments
contract, wired through that port's Phase 6 build-integration idiom.

---

## Explicitly out of scope (recorded so they aren't invented mid-implementation)

- **Transports and sender implementations themselves** — the generators consume each port's sender
  abstraction; building or changing senders/outbound routing is separate work (except where a
  port audit triggers the explicit prerequisite in step 0).
- **Mesh UI** — no UI work anywhere in this plan.
- **Contract-drift CI** — parked (7e). The hash ships; the cross-repo drift feature that consumes
  it comes later and should build on the mesh's existing diff mechanism, per the 7e note.
- **`decimal`/`format` fidelity** — parked (7d); every mapper maps what the schema says.
- **Any change to .NET generated method bodies** or to `IBenzeneMessageSender`.
- **Generating from `events[]`** — no .NET builder reads events today; parity means parity.
  A future "event publisher/subscriber codegen" is its own product decision.
- **AsyncAPI/OpenAPI export formats** — the contract document is the input; other formats stay
  where they are (`-type` on the spec endpoint).
- **`message-handlers`/`readme` output modes** — .NET extras, not part of cross-language parity.

## Cross-repo mechanics

| Work | Repo | Branch/CI notes |
|---|---|---|
| Phase 1 | Benzene (spec) | Designated session branch (given at dispatch). Website link-check must pass. Adds fixtures; never edits existing ones. |
| Phase 2 | benzene-dotnet | Normal branch discipline; re-vendors fixtures + bumps `SPEC_VERSION`; `conformance-drift-check.yml` goes green only after the spec branch merges — sequence the merges spec-first. |
| Phases 3, 6-TS, 7-TS | benzene-typescript | Port repo conventions; vendors fixtures + drift check. |
| Phases 4, 6-Py, 7-Py | benzene-python | Same. |
| Phases 5, 6-Go, 7-Go | benzene-go | Same. |
| Amendment C supersession note | benzene-dotnet (`work/archive/spec-mesh-tooling-implementation-plan-2026-08.md`) | One-line edit in Phase 1's slice or Phase 2's, whichever lands first. |

## Suggested agent task slicing

| Task | Phase(s) | Repo | Parallel-safe with |
|---|---|---|---|
| T1 | Phase 1 | Benzene | nothing that depends on the spec — do first |
| T2 | Phase 2 | benzene-dotnet | after T1 |
| T3 | Phase 3 + 6-TS + 7-TS | benzene-typescript | T4, T5 (after T1; T2 recommended first as reference) |
| T4 | Phase 4 + 6-Py + 7-Py | benzene-python | T3, T5 |
| T5 | Phase 5 + 6-Go + 7-Go | benzene-go | T3, T4 |

Each task: read §0 + the parity checklist + its phase; run the port audit before writing code;
verify cited files; commit per repo conventions; report what was verified vs assumed — especially
step-0 audit findings, which are this plan's known unknowns.

## Owner decisions needed

Kept to a minimum; everything else above is decided by this plan from the code and prior rulings.

1. **None blocking.** Two items are flagged for awareness, decided here but cheap to reverse
   before Phase 2 merges:
   - The contract-hash **value change** in .NET (Phase 2 step 4): one-time drift *warning* in
     mixed-version fleets. Accepted as the cost of a portable hash; the alternative (dual-hash
     transition machinery) buys little for a pre-1.0 ecosystem.
   - **JCS (RFC 8785)** as the canonicalization (Phase 1 step 2), diverging from mesh §2.2's
     documented-order style for the recorded reason (cross-port comparability). If the owner
     prefers stylistic consistency with mesh §2.2 over off-the-shelf canonicalizers, only Phase
     1 step 2 and its fixtures change.
