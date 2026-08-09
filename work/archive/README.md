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
