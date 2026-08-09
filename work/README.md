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

## What is archived, and why it was

See [`archive/README.md`](archive/README.md).

## What is deliberately still live

- `outstanding-bugs.md` — its "Resolved" half is history, but its **"Open — maintainer decisions"**
  half is a real backlog. Rule 2 applies: it stays until those decisions are made, then it archives.
- `1.0-release-plan.md` — ACTIVE, and the successor to everything archived from the readiness set.
- `service-mesh-roadmap-1.0.md` — the one `*-roadmap-1.0.md` that is genuinely a living document,
  updated 2026-07-25, owned by the mesh product owner, and cited from public documentation.
