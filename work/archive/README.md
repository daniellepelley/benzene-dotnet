# work/archive

Dated records and superseded documents. **Nothing here is current — do not cite any of it for status.**

Kept because superseded reasoning is worth having when the same question comes round again. See
[`../README.md`](../README.md) for the rules that decide what lands here.

## Superseded by the 2026-07-18 release assessment

A five-lens, code-verified assessment run on 2026-07-18 found the readiness documents systematically
stale — "a mix of resolved, stale, and aspirational claims with no reliable marker, citing phantom
types and already-fixed bugs". The successor is [`../1.0-release-plan.md`](../1.0-release-plan.md),
whose sign-off is code-driven rather than doc-driven.

**Readiness / API surface** — each of these already carried a superseded banner in its own text:

- `1.0-readiness-checklist.md`
- `1.0.0-release-checklist.md`
- `1.0.0-release-status.md`
- `1.0-api-readiness-review-2026-07-14.md`
- `api-surface-review.md`

**Per-area roadmaps** — these carried **no** banner. The staleness is declared only in the release
plan that replaced them, so a reader opening `aws-roadmap-1.0.md` (1,879 lines) directly had nothing
to warn them at all. That asymmetry is why these mattered more than the five above.

- `aws-roadmap-1.0.md` · `azure-roadmap-1.0.md` · `google-cloud-roadmap-1.0.md`
- `dx-roadmap-1.0.md` · `observability-roadmap-1.0.md` · `performance-roadmap-1.0.md`

Each one's newest internal date falls between 2026-07-14 and 2026-07-17 — all before the assessment.
That is the evidence the judgement applies to them.

**`service-mesh-roadmap-1.0.md` is deliberately not here.** It matches the release plan's blanket
`*-roadmap-1.0.md` wording, but its newest internal update is 2026-07-25 — a week *after* the
assessment — it is maintained as a living document rather than versioned snapshots, it is named as one
in `.claude/PRODUCT_OWNERS.md`, and public documentation cites it. The blanket judgement is simply out
of date with respect to that one file.

## Dated review and bug-hunt records (2026-07)

These are the reports that *found* things. What they found was fixed, and the fixes plus their
regression tests are the record that matters — so the reports are history, not a backlog:

- `overnight-bug-hunt-findings-2026-07.md`, `overnight-fixes-log-2026-07.md`,
  `overnight-progress-report-2026-07-14.md`
- `arch-review-2026-07/`, `bughunt-2026-07/`, `cloud-review-2026-07/` (14 per-service reviews)
- `debuggability-assessment-2026-07.md`, `audit-remaining-suggestions-2026-07.md`

**`outstanding-bugs.md` stayed live**, and it is the clearest illustration of the "extract before
archiving" rule. Its "Resolved since the prior triage" half is pure history and would archive happily.
Its **"Open — maintainer decisions"** half is a real, unresolved backlog — behaviour, API and policy
calls that nobody has made yet. Archiving the whole file would have buried that backlog under a
heading that says "do not cite". It archives once those decisions are made.

## The 2026-08-20 archive sweep

A repo-wide sweep of `work/` and `docs/plans/` (the latter folder is now gone — executed plans are
work-shaped and do not belong in the public docs tree). Every file below was verified **actioned
against code** (shipped packages, tests, examples, CHANGELOG) before moving; each carries a one-line
`> ARCHIVED` stamp naming its evidence. Live remainders were extracted first, to
[`../1.0-release-plan.md`](../1.0-release-plan.md) §9 ("Remainders extracted by the 2026-08-20
archive sweep"). Filenames carry the month each document was last true (its own internal date where
it had one, otherwise 2026-08).

**Executed designs/plans (from `work/`)** — the feature shipped; the doc is history:

- `1.0-api-freeze-proposal-2026-07.md` — API-freeze decisions A–D; executed (release plan item 1.2).
- `api-shape-proposal-1.0-2026-07.md` — API-shape items; shipped (2a/2b/4a), 4b tracked in `../outstanding-bugs.md`.
- `asp-self-hosting-design-2026-08.md` — self-hosted ASP.NET; shipped in `Benzene.AspNet.Core`.
- `auth-middleware-design-2026-07.md` — auth middleware; shipped as `Benzene.Auth.*`.
- `aws-lambda-aspnetcore-research-2026-07.md` — research; folded into `Benzene.Aws.Lambda.HttpBridge`.
- `aws-mesh-multi-transport-plan-2026-08.md` — shipped as `examples/AwsMesh`.
- `azure-functions-mesh-multi-transport-plan-2026-08.md` — shipped as `examples/AzureFunctionsMesh`.
- `azure-functions-trigger-codegen-design-2026-08.md` — shipped as `Benzene.Azure.Function.SourceGenerators`.
- `batch-failure-handling-2026-07.md` — per-transport batch containment; shipped.
- `benzene-clients-redesign-plan-2026-07.md` — outbound clients redesign; all 4 steps shipped in `Benzene.Clients`.
- `cancellation-design-2026-08.md` — cancellation initiative; all phases shipped.
- `claim-check-plan-2026-08.md` — shipped as `Benzene.ClaimCheck` (+ S3/Blob stores).
- `client-health-checks-remaining-designs-2026-08.md` — per-transport health-check rulings; recorded/implemented.
- `cross-language-clients-plan-2026-08.md` — .NET Phase 2 shipped (`Benzene.HealthChecks.Schema`).
- `customization-robustness-review-2026-08.md` — findings fixed with regression tests.
- `high-fixes-design-2026-08.md` / `medium-fixes-design-2026-08.md` — fix designs (ex-`work/designs/`); shipped.
- `inprocess-fanout-design-2026-08.md` — shipped as `InProcessFanOutClientMiddleware`.
- `internal-transport-design-2026-08.md` — shipped as `Benzene.Clients.InProcess`.
- `kinesis-batch-failure-handling-design-2026-07.md` — implemented in full in `Benzene.Aws.Lambda.Kinesis`.
- `lightweight-non-http-transport-design-2026-07.md` — Phase 1 shipped; Phase-2 go/no-go extracted.
- `mesh-drains-up-review-2026-07.md` — both phases shipped (`Mesh.Ui`, `Mesh.Collector`).
- `mesh-self-discovery-design-2026-07.md` — shipped as `Benzene.Mesh.Discovery.Aws` + `deploy/Discovery`.
- `problem-details-plan-2026-08.md` — RFC 9457 problem details; Phases 1–5 shipped.
- `request-response-design-review-2026-07.md` — executed via the request/response improvements plan.
- `response-as-event-design-2026-07.md` — shipped as `Benzene.ResponseEvents`.
- `runtime-test-payloads-plan-2026-08.md` — decision 1(c) shipped (`Benzene.Aws.Lambda.TestPayloads`).
- `saga-design-2026-07.md` — shipped as `Benzene.Saga` (its "no code yet" header lagged reality).
- `spec-mesh-interconnection-dx-assessment-2026-08.md` — executed via the tooling implementation plan.
- `spec-mesh-tooling-implementation-plan-2026-08.md` — shipped (`Benzene.Descriptor`, `CodeGen.Build`/`.Client`).
- `startup-diagnostics-dx-2026-07.md` — dated assessment; the start-up check machinery ships in `src`.
- `topic-prefix-migration-2026-07.md` — DONE 2026-07-25; reserved topics carry `benzene:`.
- `versioning-finish-and-dogfood-plan-2026-08.md` — versioning finished and dogfooded.
- `deployment-descriptor-spike-2026-08/` — the runnable spike (ex-`work/spikes/`); superseded by the
  shipped `src/Benzene.Descriptor`. The living design record stays at `../deployment-descriptor-design.md`.

**Actioned with remainders extracted to `../1.0-release-plan.md` §9:**

- `asyncapi-alignment-2026-08.md` — Stages 1–2 shipped; F5 (reply-channel gating) extracted.
- `client-health-checks-design-2026-08.md` — first increment shipped; per-transport checks extracted.
- `complex-payloads-byo-schema-plan-2026-08.md` — shipped except Phase 4.4 (spec-gated); extracted.
- `cross-platform-design-review-2026-07.md` — largely shipped; StartUp deprecate-vs-keep + CLI/spec-retrieval gap extracted.
- `examples-testing-plan-2026-08.md` — Phase-3 CI confirmation + real-dependency tier extracted.
- `gaps-found-building-the-pattern-examples-2026-08.md` — gap 4 + BenzeneHost embedded-shape question extracted.
- `health-checks-1.0-review-2026-08.md` — transport token-seeding + test gaps extracted.
- `outbox-plan-2026-08.md` — shipped as `Benzene.Outbox*`; release-posture question extracted.

**Spec content moved to its right place:**

- `settlement-contract-1.0-2026-07.md` — the normative settlement contract is now carried directly by
  [`docs/capability-matrix.md`](../../docs/capability-matrix.md); this file is the migration record.

**Executed plans from the former `docs/plans/`** (each verified shipped; see each stamp):

- `dynamodb-streams-plan-2026-08.md` · `eventbridge-plan-2026-08.md` · `grpc-enhancement-plan-2026-08.md`
- `pattern-support-plan-2026-08.md` · `payload-testing-ui-plan-2026-08.md` · `polly-resilience-plan-2026-08.md`
- `rabbitmq-plan-2026-08.md` · `request-response-improvements-plan-2026-07.md` · `response-events-plan-2026-07.md`
- `results-taxonomy-plan-2026-08.md` · `streaming-plan-2026-08.md` · `terraform-eventbridge-rules-plan-2026-08.md`

## Dated review and fix-design record (2026-08-25)

- `bug-fix-designs-2026-08.md` — the design ruling (decision, rationale, rejected alternatives) for
  the 27 evidence-backed findings from the round-5/round-6 adversarial review passes (shared task
  board #1–#27). All nine work packages landed and pushed; the per-finding summaries now live in
  [`../outstanding-bugs.md`](../outstanding-bugs.md)'s Resolved section, each pointing back here for
  the full record — that's why this file is kept rather than deleted, unlike the pure bug-hunt
  reports above.

## Dated review and fix-design record (2026-08-26)

- `bug-fix-designs-round7-10-2026-08.md` — the design ruling (decision, rationale, rejected
  alternatives) for the evidence-backed findings from the round-7–10 adversarial review passes (shared
  task board #30–#95), successor to the round-5/6 ruling above. All 16 work packages (WP-A through
  WP-U) landed and pushed to `main` via 16 merge commits, followed by one cleanup commit (`f719504`)
  fixing baseline regressions the full post-merge test run surfaced. Archived 2026-08-26 after an
  independent re-verification of the build/test baseline (`Benzene.Core.Test` 3017/2/0,
  `Benzene.Mesh.Test` 535/535, `Benzene.Mesh.Host.Test` 141/141, `Benzene.Examples.sln` 0 errors); the
  per-finding summaries live in [`../outstanding-bugs.md`](../outstanding-bugs.md)'s Resolved section
  (search "Tracked findings round 7–10"), each pointing back here for the full record.
