> ARCHIVED 2026-08-30: actioned. The round-13 blind re-audit record (task board #198–#203: a
> controlled experiment re-reviewing `Benzene.RateLimiting`/`Benzene.Cache.Core`/`.Redis` blind to
> round 11's prior findings, to measure whether the find-rate genuinely declines on re-review). Its
> worth-fixing findings (#198–202) landed as WP-J of the rounds 12–14 fix plan and were pushed to
> `main` (`aab6f7b`) — see
> [`bug-fix-plan-rounds12-14-2026-08.md`](bug-fix-plan-rounds12-14-2026-08.md) for the fix plan.
> Per-finding summaries live in `work/outstanding-bugs.md` (search "Tracked findings rounds 12–14,
> WP-J"), each pointing back here for the full record.

# Round 13 — blind re-audit experiment (2026-08)

**Status: ACTIVE — findings only; fix designs now ruled in [`bug-fix-rulings-round12-13-2026-08.md`](bug-fix-rulings-round12-13-2026-08.md) (#198–#202) — not yet implemented.** This round is different in kind from rounds 1–12:
it is not new-ground coverage. It is a controlled experiment answering a specific question the user
asked after round 12: *if we're genuinely fixing bugs, shouldn't the find-rate decline when we
re-review the same code?* Task board **#198–#202** (3 worth-fixing, 2 minor), plus round-summary
**#203**.

## The experiment

`Benzene.RateLimiting` + `Benzene.Cache.Core`/`.Redis` was round 11's highest-bulk single area — 15
findings (#133–147: 9 worth-fixing, 6 minor), all fixed and merged. One agent was sent back into the
same two packages at commit `104d371` (post-fix, fully merged and pushed), in an isolated worktree,
with two rules designed to make the test honest:

1. **Blind.** It was explicitly told not to read `work/outstanding-bugs.md`, the archived round-11
   ruling doc, or anything else describing prior findings, until *after* it had written its own
   complete findings list — so it could not simply check off a known list.
2. **Equal rigor.** Same standard as every round-11/12 agent: read every file fully, execute real
   adversarial probes (concurrency stress, disposal races, malformed config, delegate exceptions),
   not just reason about the code.

After writing its list, the agent did its own honest cross-check against `outstanding-bugs.md` and
reported which findings it believed were genuinely new vs. already-known. I independently re-verified
that self-assessment against the round-11 finding list rather than trusting it outright (below).

## The result: bulk declined 67%, and every survivor is explainable

**15 → 5.** The blind pass found **5** findings total (3 worth-fixing, 2 minor) against round 11's
15. Independent verification confirms the agent's own cross-check was accurate: none of the 5
overlaps with anything in `outstanding-bugs.md`'s round-11 section, and one thing the agent initially
flagged (cache-sync cancellation propagating past a successful DB write) turned out to be `#139`'s
own documented, deliberate design — the agent caught this itself before finalizing its list, which is
itself a good signal about how carefully it worked.

**Worth-fixing (genuinely new):**
- **#198** — `RedisCacheService.CreatePrefixActions` (`src/Benzene.Cache.Redis/RedisCacheService.cs:109-117`)
  builds the wildcard-invalidation pattern as `EscapeGlobLiteral(prefix) + "*"` with no check that
  `prefix` is non-empty. An empty or whitespace-only prefix (a missing tenant id, an unset config
  value) produces the literal pattern `"*"`, which `RedisWildcardActions.InvalidateEntryAsync` then
  deletes in batches — **every key in the logical database**. Verified via probe: `CreatePrefixActions(string.Empty)`
  produces `"*"` and a real `KEYS *` scan confirms it matches everything. This was never touched by
  round 11 (that round's Redis findings were `RedisMultiKeyActions`/`RedisCacheService.DisposeAsync`
  — different methods entirely) — a genuinely fresh, serious finding: one bad string interpolation
  away from a full cache wipe with zero guard rail.
- **#199** — `CacheWriteActions.WriteThroughAsync`'s 3-arg overload (`src/Benzene.Cache.Core/CacheWriteActions.cs:61-94`)
  runs the caller-supplied `getCacheAction`/`getCacheValue` delegates **outside** the try/catch that
  `SyncCacheAfterWriteAsync` wraps the actual cache I/O in — the exact protection `#139`'s fix added,
  just applied one call too narrowly. Verified: a `getCacheAction` that throws after a successful DB
  write propagates that exception in place of the result, indistinguishable at the call site from the
  database write itself having failed — precisely the failure mode `#139` was fixed to prevent, just
  reachable through a different, adjacent code path the original fix didn't cover.
- **#200** — the "one internally-owned rate limiter" guard added by round 11's `#133` fix
  (`UseInternallyOwnedRateLimiting`, `src/Benzene.RateLimiting/Extensions.cs:256-279`) is keyed on the
  shared `IBenzeneServiceContainer`, but `MiddlewarePipelineBuilder<T>.Create<TNewContext>()`
  deliberately shares that same container across sibling pipelines for one service's multiple
  transports — exactly the supported multi-transport pattern. Verified: building two unrelated
  pipelines off one container, each with its own `UseFixedWindowRateLimiting`, throws
  `InvalidOperationException` on the second, even though the docs and exception message both describe
  the guard as "per pipeline." This is a bug **introduced by round 11's own leak fix**, not a
  pre-existing one — the guard didn't exist before `#133` was fixed.

**Minor (genuinely new):**
- **#201** — negative caching (`#140`'s fix) silently breaks for any injected `ISerializer` (the seam
  round 11's `#145` fix added) that encodes `null`/a default value as an empty string, since presence
  detection is `!string.IsNullOrEmpty(cacheValue)`. Reintroduces the exact cache-penetration scenario
  `#140` closed, silently, for a class of custom serializer the docs invite but don't warn against.
- **#202** — `RateLimitingMiddlewareBase.HandleAsync` catches `ObjectDisposedException` around both the
  cost delegate and `Acquire()` in one block (per `#143`'s deliberate fix, which moved the cost
  delegate inside this guard) and always reports "the rate limiter has already been disposed" — false
  when the exception actually came from an unrelated disposed dependency inside the cost delegate. A
  direct, minor precision gap in `#143`'s own trade-off.

## What this shows

This is real evidence for the decline hypothesis, with an important nuance: **two of the three
worth-fixing findings are second-order residue from round 11's own fixes**, not undiscovered bugs in
the original code. `#200` and `#202` didn't exist before round 11 — they're new code (the DI ownership
guard, the widened try/catch) that itself has an edge case. `#198` is the one genuinely missed
pre-existing bug; `#199` and `#201` are protections that were added in one place but not extended to
an adjacent path.

That means the "floor" this process approaches per area isn't literally zero — every fix is itself new
code with its own (smaller) bug surface, so a third pass after fixing #198–202 would likely find a
residual few, not zero, and that's expected rather than a failure of the method. What should decline,
and did here, is the *magnitude*: 15 → 5 → (predicted) smaller still. Convergence is asymptotic per
area, not a hard finish line — the productive stopping point is where the next pass's expected yield
drops below the cost of running it, not literal zero.

## Next steps

Two honest options, not mutually exclusive:
1. Fix #198–202, then run one more blind re-audit of the same two packages to see whether the decline
   continues (5 → fewer) — the strongest single piece of evidence either way.
2. Apply this same audit → fix → blind-re-audit cycle to other high-bulk round-11/12 areas (Mesh
   discovery/catalog at 10, the auth layer at 17 combined, or Mesh.Dispatch's newly-found gaps) to
   build a broader picture rather than over-indexing on one area.
