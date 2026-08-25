# work

Design and planning notes for the .NET implementation.

## Two kinds of document

Every file in here is one of two things, and the difference decides where it lives.

**Living.** Someone owns it, it is kept true, and you may cite it. Design decisions still in force,
roadmaps still being updated, plans still being worked. These sit in `work/`.

**Dated.** A record of one moment — an audit, a bug hunt, a review pass, a status snapshot. It was
true the day it was written and has been decaying ever since. It is never updated, only superseded.
These belong in `work/archive/`.

The failure this exists to prevent is the one we had: a directory where 1,879-line roadmaps full of
resolved, stale and aspirational claims sat next to documents that were current, with nothing to tell
them apart. Anyone reading — human or agent — had to already know which was which.

## The rules

1. **A dated document goes to `work/archive/` and carries its date.** If the filename does not say
   when it was true, add it: `debuggability-assessment-2026-07.md`, not `debuggability-assessment.md`.

2. **Extract before archiving.** A review or bug hunt is archived once every finding is either fixed
   with a regression test, or written down somewhere live as an open decision. The archive is a record
   of *reasoning*, not a hiding place for a to-do list. Find the bugs, fix the bugs — the fix and its
   test are the record that matters; the report that found it is history the moment it is actioned.

3. **Nothing in `work/` may tell the reader not to trust it.** A document that needs a
   "⚠️ SUPERSEDED — do not cite this" banner has already answered the question of where it belongs.
   The banner is the symptom; `archive/` is the fix.

4. **When a living document is superseded, move it and name the successor** — one line at the top of
   the archived copy, so the trail is followable.

5. **One home per document.** A document lives in the repo that owns its subject, and other repos link
   to it rather than keeping a copy. Two copies diverge silently; we have already proved this — the
   split left `work/` duplicated across repositories and eleven files drifted apart before anyone
   noticed.

6. **Archiving is not deletion.** Superseded reasoning is worth keeping — it is how you avoid
   relitigating a decision in six months. It just does not belong in the same drawer as the truth.

## Documents that live in the specification repo

Rule 5 (one home per document) cuts both ways. These were duplicated here by the repo split and have
moved out to [`daniellepelley/Benzene`](https://github.com/daniellepelley/Benzene/tree/main/work),
because their subject is the language-neutral contract or the shared UI, not the .NET implementation:

- `benzene-vision.md` · `benzene-naming-principle.md` · `benzene-headers-design.md` ·
  `benzene-headers-plan.md` · `error-payload-proposal.md` · `cloudevents-design.md` ·
  `spec-review-2026-07-25.md` · `mesh-ui-product-vision.md`

The copies here were not merely redundant. `benzene-naming-principle.md` in this repository still
described the abandoned `benzene-topic` header spelling; the specification repo's copy records the
2026-07-27 reversal back to `topic`, which is what actually shipped. A stale second copy of a contract
document is worse than no copy.

## What is archived, and why it was

See [`archive/README.md`](archive/README.md).

## What is deliberately still live

Everything in `work/` (outside `archive/`) is a living document after the 2026-08-20 archive sweep,
which moved every actioned plan/design/review to `archive/` (extracting live remainders first — see
`1.0-release-plan.md` §9, "Remainders extracted by the 2026-08-20 archive sweep"). The sweep also
retired `docs/plans/` (executed plans in the public docs tree) into `archive/`, moved the
`work/spikes/deployment-descriptor/` spike (superseded by the shipped `src/Benzene.Descriptor`), and
moved the delivered designs that used to stay here for their `src/` citations — the sweep repointed
every citation to the archive path, so the trail rule 4 preserves is intact.
What remains, and why it is live:

- `1.0-release-plan.md` — ACTIVE; the master release plan, the successor to everything archived from
  the readiness set, and now also the home of the sweep-extracted remainders (§9).
- `outstanding-bugs.md` — its "Resolved" half is history, but its **"Open — maintainer decisions"**
  half is still a real backlog. Four of those decisions have since been made and implemented (the
  split-brain `RaiseOnFailureStatus` defaults, the `AddMessageHandlers` finder lock-in,
  `BenzeneResultExtensions.IsSuccess()`, and the now-removed `BenzeneHttpWorker` entry) and are recorded
  as such; the rest are open. Rule 2 applies: it stays until those decisions are made, then it archives.
- `service-mesh-roadmap-1.0.md` — the one `*-roadmap-1.0.md` that is genuinely a living document,
  owned by the mesh product owner and cited from public documentation.
- `benzene-clients-vision.md` — standing vision document for the clients story.
- `benzene-outbound-model-plan.md` — in-flight plan (the outbound model the descriptor tool needs).
- `benzene-result-errors-ruling.md` — standing decision record, cited by `docs/migration-alpha-to-1.0.md`.
- `bug-fix-designs-2026-08.md` — ACTIVE ruling + implementation plan for the 27 evidence-backed
  findings from the 2026-08 review rounds 5–6 (shared task board #1–#27). Archives when all nine
  work packages land.
- `deployment-descriptor-design.md` — living design record behind `src/Benzene.Descriptor`, cited by
  `docs/contract-artifacts.md`.
- `enterprise/` — slices 0, 1, 2, 3 and 5 have shipped; **slice 4** and the "Deferred — deliberately in
  no slice yet" list are the live part.
- `inprocess-modular-monolith-scope.md` — standing scope document for the in-process story.
- `new-mesh-sources-design-2026-08.md` — current design work (dated in its name by convention).
- `otel-fleet-adapter-scope.md` — standing scope document.
- `settlement-default-alignment-proposal.md` — Tier A is done, but **Tier B** (the cross-transport
  null/unrouted `!= true` vs `== false` policy) is an undecided maintainer call and **Tier C** is an
  outstanding docs task. It stays live for those two; the settled contract it builds on lives in
  `docs/capability-matrix.md` (the migration record is archived).
- `testing-tooling-investigation.md` — open investigation (two spikes + recommendation still owed).
