# Benzene.Saga

## What this package does
An in-code **saga orchestrator** for distributed transactions across services: run a sequence of
stages that either **completes in full or rolls back in full**, leaving no orphaned records, so the
whole operation can be safely retried. It's the generalized, sustainable successor to the original
commercial Benzene saga code (`Legacy/Benzene.Framework/Saga` in the `BenzeneImport` repo) — see
`work/archive/saga-design-2026-07.md` for the design and the decisions taken.

## Capability boundary — in-process only, NO durable crash-resume
This is a deliberate boundary, not a gap (see `work/1.0-release-plan.md` §2 and the
[Capability Matrix](../../docs/capability-matrix.md)):
- **In-process, in-memory execution.** A saga runs to completion or rollback within a single
  `RunAsync` call. Steps are in-memory closures; they cannot be serialized or rehydrated.
- **The `ISagaStateStore` is for observability/operational recovery, not recovery-by-replay.** It
  records start/stage/finish progress so you can see where a saga got to; it does **not** let the
  engine resume a crashed saga. If the process dies mid-saga, the effects already applied are not
  automatically compensated — that needs manual reconciliation using the recorded state.
- **For crash-durable long-running orchestration, use a durable workflow engine outside Benzene** —
  AWS Step Functions, Azure Durable Functions, Temporal, or similar, which persist workflow state
  between steps and resume after a crash. Benzene sagas suit short, in-process compensation flows
  where a full-process failure is acceptable to reconcile manually.

## The model
```
Saga  ── ordered ──▶  Stage  ── concurrent ──▶  Step (forward + compensation)
```
- **Step** (`SagaStep<T>`) — a forward action (`Func<SagaContext, Task<IBenzeneResult<T>>>`) paired
  with an optional compensation (`Func<SagaContext, T, Task<IBenzeneResult>>`). Success is
  `IBenzeneResult.IsSuccessful`. A forward that throws is caught and treated as a failed step.
  Compensation runs during rollback **only if the step succeeded**; a succeeded step with no
  compensation is treated as "nothing to undo".
- **Stage** — an N-sized group of steps run concurrently (`Task.WhenAll`); succeeds only if every
  step succeeds. On its own failure it compensates its concurrently-succeeded steps.
- **Saga** — runs stages in order, threading each stage's results into a shared `SagaContext`
  (typed bag, `ctx.Get<T>()`) so a later stage can use an earlier stage's output. On the first
  stage failure it compensates every completed effect in **reverse (LIFO) order** — the failed
  stage's succeeded steps first, then each completed stage newest-first — then returns a
  `SagaResult`.

## Key types
- `new SagaBuilder()` → `StageBuilder` → `StepBuilder<T>` → `.Build()` returns a `Saga` — the fluent
  API. (Entry is `new SagaBuilder()`, not `Saga.Define()`: a bare type named `Saga` inside namespace
  `Benzene.Saga` is shadowed by the namespace from any `Benzene.*` caller — same reason
  `Benzene.Results` names its type `BenzeneResult`, not `Results`.)
- `SagaContext` — typed result bag; steps publish their result after their stage succeeds.
- `SagaResult` — `Outcome` (`Succeeded` / `RolledBack` / `PartiallyRolledBack`), `IsSuccess`,
  `FailedStageIndex`, `Failure` (the failing step's result, first-item for compatibility — see
  `Failures` below), `FailureException`, and `CompensationFailures` — `IReadOnlyList<SagaStepOutcome>`,
  the run-scoped outcomes of steps whose compensation itself failed (orphaned effects to attend to).
  Two additive members: `Failures` — `IReadOnlyList<SagaStepOutcome>`, every step that failed within
  the failing stage (non-empty entries beyond the first exactly when more than one step in that stage
  failed concurrently — a normal outcome, since a stage's steps all run concurrently and are all
  awaited before the stage is judged failed; `Failure`/`FailureException` mirror `Failures[0]` and stay
  populated exactly as before). `StateStoreFailure` — `Exception?`, the exception a configured
  `ISagaStateStore` call threw during this attempt, or `null` if none did — see "State-store failure
  handling" below.
- `SagaStepOutcome` — an immutable, per-run snapshot of one step's outcome (`Step`, `State`, `Result`,
  `Exception`), returned by `ISagaStep.ExecuteAsync`/`CompensateAsync` instead of being stored on the
  step. This is the run-scoped state object the immutability/concurrency-safety contract above depends
  on.
- `SagaStepState` — `Pending` / `Succeeded` / `Failed` / `RolledBack` / `CompensationFailed`.
- `Saga.RunAsync(SagaRunOptions)` — the opt-in overload for the §7 fast-follows (`RunAsync()` stays
  the zero-overhead default: one attempt, no store, no id). `SagaRunOptions` carries an optional
  `SagaId`/`Name`, an `ISagaStateStore`, and a `SagaRetryPolicy`.
- `SagaRetryPolicy` (§7.5) — whole-saga retry: `MaxAttempts` (total attempts, 1 = no retry),
  `InitialDelay`, `BackoffFactor`, and an internal `Delay` seam for tests. Retry fires **only** on
  `RolledBack` (a clean rollback is safe to re-run from scratch); `Succeeded` needs none and
  `PartiallyRolledBack` must not be re-run (would double-apply orphaned effects).
- `ISagaStateStore` (§7.4) — pluggable progress/outcome sink: `RecordStartedAsync` →
  `RecordStageCompletedAsync` (per completed stage) → `RecordFinishedAsync`, once per attempt.
  Records progress for durable **observability/operational recovery**; it does **not** resume a saga
  (in-memory step closures can't be serialized/rehydrated). `InMemorySagaStateStore` (event list +
  `EventsFor(sagaId)`) is the built-in test double; a durable adapter is a 3-method copy-paste (see
  the cookbook). `SagaRunInfo`/`SagaStateEvent`/`SagaStateEventKind` are the data model.

## State-store failure handling
A configured `ISagaStateStore` call throwing — `RecordStartedAsync`, `RecordStageCompletedAsync`, or
`RecordFinishedAsync`, at any point during `RunOnceAsync` — is caught and never allowed to abort the
run, skip rollback, or replace the saga's real outcome with a raw exception; it is surfaced instead via
the additive `SagaResult.StateStoreFailure` (only the first failure this attempt is kept, but every
store call is still attempted regardless of an earlier one having failed):
- **A throw after an effect-producing stage completes** no longer aborts with zero rollback attempt —
  compensation for every completed stage still runs exactly as it would if the store had succeeded, and
  the resulting `RolledBack`/`PartiallyRolledBack` result carries `StateStoreFailure` alongside the real
  `CompensationFailures`.
- **A throw from the final `RecordFinishedAsync` after every stage genuinely succeeded** no longer
  discards the entire successful `SagaResult` in favor of a raw exception — the caller still gets back
  `Outcome == Succeeded` (with `StateStoreFailure` populated), so a caller that reasonably retries on any
  thrown exception can no longer re-run an already-succeeded saga with no compensation and no dedup. The
  same symmetric handling applies to `RecordFinishedAsync` on the failure path, so a store failure there
  can't silently drop `CompensationFailures` visibility either.

A populated `StateStoreFailure` says nothing about whether the saga's own steps succeeded — always read
it alongside `Outcome`/`Failures`/`CompensationFailures`, which reflect the saga's real forward/rollback
progress independent of whether the store durably recorded it.

## Design decisions (from `work/archive/saga-design-2026-07.md` §7)
- **Await-all within a stage** (not fail-fast) — deterministic; every step's outcome is known
  before deciding to compensate.
- **Best-effort rollback** — every compensation is attempted even if one fails; failures surface as
  `PartiallyRolledBack` + `CompensationFailures` rather than stranding the rest.
- **Typed `SagaContext`** for threading results between stages.
- **In-process execution** — the engine runs a saga to completion/rollback in one call. Progress can
  be recorded to a pluggable `ISagaStateStore` (shipped fast-follow) for durable observability, but
  the engine does **not** resume in-memory step closures after a crash (they can't be serialized) —
  no DB dependency is baked into core. Optional whole-saga retry via `SagaRetryPolicy` (shipped).
- **Client-agnostic engine** — depends only on `Benzene.Abstractions` + `Benzene.Results`, **not**
  `Benzene.Clients`. Because `IBenzeneMessageSender.SendAsync(topic, req)` already returns
  `Task<IBenzeneResult<T>>`, a step's `Do(...)` calls it directly — no adapter package needed. An
  HTTP call or any async action returning an `IBenzeneResult<T>` works identically.

## When to use
- Multi-step operations spanning several services where partial completion must not leave orphaned
  records (e.g. a signup that creates a tenant, then a user, then RBAC roles across services).

## Dependencies on other Benzene packages
- **Benzene.Abstractions** — `IBenzeneResult` / `IBenzeneResult<T>`.
- **Benzene.Results** — `BenzeneResult` factory (used to synthesize a failure result when a forward throws).

## Conventions / notes
- Concurrency safety: steps in a stage run concurrently but only **read** earlier stages' context
  values during that phase; writes happen single-threaded after each stage barrier (`Stage.Publish`),
  so `SagaContext` needs no locking.
- **A built `Saga` is immutable and safe for concurrent `RunAsync()` calls.** `ISagaStep`/`SagaStep<T>`/
  `Stage` are read-only descriptors after `Build()` — no per-execution outcome (a step's state, result,
  exception) is ever stored on them. Every run's outcome lives in a `SagaStepOutcome`, created fresh and
  threaded through as a run-scoped list local to that one `RunAsync`/`RunOnceAsync` call, so the same
  built `Saga` instance can be reused — including run concurrently, any number of times — without one
  run's outcome corrupting another's. (Pre-fix, outcome fields lived on the step/stage instances
  themselves, shared across concurrent runs — a round-5 stress test reproduced 6/300 corrupted runs;
  see `work/bug-fix-designs-2026-08.md` WP-7(e).) `SagaResult.CompensationFailures` is
  `IReadOnlyList<SagaStepOutcome>`, not `IReadOnlyList<ISagaStep>` — a per-run snapshot, not a live
  reference to shared state.
- Test coverage lives in `test/Benzene.Core.Test/Saga/` — `SagaTest.cs` (happy path, mid-stage
  failure + rollback, cross-stage LIFO rollback, compensation-failure → `PartiallyRolledBack`,
  forward-throws, a 300-concurrent-`RunAsync()` stress test on one built `Saga` asserting zero
  cross-run contamination, and — `#209` — two steps in the same stage failing concurrently surfaces
  both in `SagaResult.Failures` while `Failure`/`FailureException` still mirror the first),
  `SagaStepTest.cs` (a step instance returns an independent outcome per call, even under concurrent
  calls) and `SagaRetryAndStateStoreTest.cs` (retry recovers a flaky step, exhausts to `RolledBack`,
  refuses to retry `PartiallyRolledBack`; state store records start/stage/finish, only completed
  stages on failure, one `Started` per retry attempt, and generates an id when none given; a
  `ThrowingSagaStateStore` test double exercises "State-store failure handling" above — `#208`: a
  throw right after an effect-producing stage completes still rolls back and surfaces
  `StateStoreFailure`, and a throw from `RecordFinishedAsync` after rollback still returns
  `CompensationFailures`; `#257`: a throw from `RecordFinishedAsync` after full success still returns
  `Outcome == Succeeded` with `StateStoreFailure` populated, and a configured retry policy correctly
  does not re-run it).
- Vocabulary note: the legacy code called the forward+compensation unit a "Part" and the parallel
  group a "Step"; this package renames them **Step** and **Stage** respectively.
