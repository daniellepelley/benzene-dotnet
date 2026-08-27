# Settlement consistency — the decided policy, and the plan to apply it

**Date:** 2026-08-25
**Status:** ACTIVE — this is the implementation driver for the settlement/acknowledgement backlog.
**Audience:** the agent implementing these changes. Read §0 and §1 in full before touching code.

> ## Why this document exists
>
> Message settlement — when a transport acks, nacks, redelivers or checkpoints past a message — has
> been changed repeatedly in this repo, and in **both directions**. The self-hosted SQS consumer's
> `WholeBatch` default was flipped to `PerMessage`; the Service Bus worker's `AutoComplete` was
> flipped to `Explicit`; `RaiseOnFailureStatus` was flipped `false` → `true` on ten adapters; the
> `AddMessageHandlers` finder change was reverted once before landing. Each flip was individually
> defensible. Collectively they are the project's single biggest source of churn, and every one of
> them changes whether a production message is **lost** or **replayed forever**.
>
> **So the rule this document exists to enforce: the implementing agent never makes a settlement
> decision. It applies the table in §1 and nothing else.** If a change you are about to make is not
> in that table, stop and ask — do not infer the policy from a neighbouring adapter, and do not
> "tidy up" an inconsistency you find. Some of the inconsistencies are deliberate carve-outs, and
> they are marked as such in §1.

---

## 0. Hard rules for the implementing agent

1. **The §1 table is the only source of truth.** Code, docs and tests all derive from it. If code and
   this table disagree, the table wins and the code changes. If this table and a *decision* recorded
   in §5 disagree, stop — that is a contradiction only the maintainer can resolve.
2. **Never flip a polarity that §1 does not tell you to flip.** In particular, the ack-on-null
   behaviour of Kafka (all three adapters) and the fan-in streams is a **deliberate carve-out**, not
   an oversight. It looks exactly like the bug you are fixing elsewhere. It is not.
3. **Every carve-out keeps an in-code comment saying why.** `Benzene.Aws.Lambda.Kafka/KafkaApplication.cs:109-114`
   is the model: it states the rule, the reason (no per-record DLQ), and the consequence of getting it
   wrong (infinite partition replay). Preserve those comments; do not shorten them.
4. **One batch per commit, in the order given.** Each batch in §2 is independently verifiable and
   independently revertable. Do not combine batches.
5. **No new settlement flags, options or enum values.** Every change below is a polarity change, a
   test, or a documentation line. If you believe a new option is needed, that is a design change —
   record it in §5 as an open question and move on.
6. **Do not edit `test/conformance-fixtures/**`.** Per `AGENTS.md`, those are vendored from the
   `benzene` repo and CI-drift-checked.
7. **If a test fails in a way that suggests the policy is wrong, stop and report.** Do not adjust the
   policy to make a test pass, and do not adjust a test to make the policy pass, unless §2 explicitly
   tells you that test's expectation changes.

---

## 1. The settlement policy table — the single source of truth

Two handler outcomes drive settlement, and they are **separate axes**. Keep them separate; most past
confusion came from conflating them.

- **Failure result** — the handler ran and returned `IsSuccessful == false`.
- **Null / unestablished outcome** — no result was recorded. Overwhelmingly this means an **unrouted**
  message: no handler matched the topic.

**The decided policy for the null axis (maintainer, 2026-08-25):** *a null/unestablished outcome is
not success. Retain it wherever a redelivery backstop exists to catch it; ack it only where retaining
it would be an unbreakable poison loop.* This closes Tier B of
[`work/settlement-default-alignment-proposal.md`](settlement-default-alignment-proposal.md).

| # | Adapter | Failure result | Null / unrouted | Action |
|---|---|---|---|---|
| 1 | `Benzene.Aws.Lambda.Sns` | escalate | **retain** | **FLIP** |
| 2 | `Benzene.Aws.Lambda.S3` | escalate | **retain** | **FLIP** |
| 3 | `Benzene.Aws.Lambda.EventBridge` | escalate | **retain** | **FLIP** |
| 4 | `Benzene.Azure.Function.QueueStorage` | escalate | **retain** | **FLIP** |
| 5 | `Benzene.Azure.Function.EventGrid` | escalate | **retain** | **FLIP** |
| 6 | `Benzene.GoogleCloud.Functions.PubSub` | escalate | **retain** | **FLIP** † |
| 7 | `Benzene.RabbitMq` (worker) | nack | **retain (nack)** | **FLIP** † |
| 8 | `Benzene.Azure.Function.ServiceBus` (AutoComplete escalation path) | escalate | **retain** | **FLIP** † |
| 9 | `Benzene.Aws.Lambda.Sqs` | retain | retain | already correct |
| 10 | `Benzene.Aws.Sqs` (consumer) | retain | retain | already correct |
| 11 | `Benzene.Aws.Lambda.DynamoDb` | retain | retain | already correct |
| 12 | `Benzene.Azure.ServiceBus` (worker) | abandon | abandon | already correct |
| 13 | `Benzene.Azure.Function.ServiceBus` (Explicit ack path) | abandon | abandon | already correct |
| 14 | `Benzene.Aws.Lambda.Kafka` | report failure | **ack** | **CARVE-OUT — do not touch** |
| 15 | `Benzene.Azure.Function.Kafka` | escalate | **ack** | **CARVE-OUT — do not touch** |
| 16 | `Benzene.Kafka.Core` (worker) | escalate | **ack** | **CARVE-OUT — do not touch** |
| 17 | `Benzene.Azure.Function.EventHub` | escalate | **ack** | **CARVE-OUT — do not touch** |
| 18 | `Benzene.Azure.EventHub` (worker) | escalate | **ack** | **CARVE-OUT — do not touch** |
| 19 | `Benzene.Aws.Lambda.Kinesis` | n/a — fan-in | n/a — fan-in | **docs only** (Batch 3) |
| 20 | `Benzene.Azure.Function.Cosmos` | n/a — fan-in | n/a — fan-in | **docs only** (Batch 3) |

**† Three adapters the original proposal's Tier B list did not name. They are included here
deliberately, and the maintainer should veto any they disagree with before Batch 1 starts:**

- **Pub/Sub (6)** — simply absent from the proposal's list. Pub/Sub subscriptions support
  dead-letter topics, so it meets the stated "a backstop exists" test exactly as SNS does. Omitting
  it looks like an oversight, not a decision.
- **RabbitMQ (7)** — the proposal *does* list it, but note this reverses behaviour that is currently
  **documented and explicitly tested** as deliberate (`RabbitMqWorkerTest.NoResultRecorded_Acks`).
  RabbitMQ has a DLX and a bounded single requeue, so it meets the test. Because this overturns a
  written decision rather than filling a gap, it carries its own line in §5's decision register.
- **Service Bus Functions (8)** — not in the proposal, and the strongest case of the three: it
  contradicts itself today. Its **Explicit**-ack path (`ServiceBusApplication.cs:136`) uses `!= true`
  with the comment *"matching the SQS reference so an unestablished outcome errs toward redelivery,
  not silent completion/loss"*, while its **AutoComplete** escalation path — now inherited from
  `AzureFunctionBatchApplicationBase:172` — uses `== false`. The same transport answers the same
  question two different ways depending on ack mode, and the worker (12) agrees with the Explicit
  path. Giving Service Bus the escalate-on-null policy makes it internally consistent; its existing
  `ShouldEscalateFailure` override (`ServiceBusApplication.cs:157`) already stops the two ack modes
  double-settling.

**Where each policy is enforced.** Five of these adapters no longer hold their own guard — SNS/S3/
EventBridge share `SingleContextEscalatingApplicationBase`, and QueueStorage/EventGrid/ServiceBus/
Kafka/EventHub share `AzureFunctionBatchApplicationBase`. The second of those mixes flip rows and
carve-out rows in one place, so the policy has to become an explicit override there rather than a
polarity. Batch 1 has the detail; do not edit either base class before reading it.

**Why the carve-outs (14–18) are real, in one sentence:** none of them has a per-record dead-letter
path, so "retain an unrouted record" means replaying the partition or shard from that offset forever
— the failure mode is worse than the one being fixed, and it takes the whole consumer down with it.

**Why fan-in (19–20) is different again:** Kinesis and Cosmos hand the *whole batch* to one handler
and never inspect a per-record result, so there is no null-vs-failure axis to have a policy about.
Failure is signalled by throwing, or by withholding the checkpoint. That is a contract to document,
not a gap to fix.

---

## 2. The work, in batches

Each batch: make the change, run the stated verification, commit, then move to the next. `dotnet test
test/Benzene.Core.Test/Benzene.Test.csproj` is the main loop; if `dotnet` is unavailable, say so and
fall back to CI (`.github/workflows/build-benzene.yml`) rather than guessing.

### Batch 1 — apply the null/unrouted policy (rows 1–8)

**Read this before editing: the guards have been consolidated since the older design docs were
written.** Five of the eight adapters no longer own their escalation guard — it now lives in one of
two shared base classes. That consolidation is a good thing and this plan builds on it rather than
re-scattering the logic, but it means **one of the two base classes is shared with carve-out
adapters, so it cannot be flipped blanket.** Check the enforcement point before you edit:

| Enforcement point | Adapters it governs | Change |
|---|---|---|
| `src/Benzene.Aws.Lambda.Core/SingleContextEscalatingApplicationBase.cs:91` | SNS, S3, EventBridge — **all three want the flip** | **Blanket flip.** `== false` → `!= true`, one edit covers rows 1–3 |
| `src/Benzene.Azure.Function.Core/AzureFunctionBatchApplicationBase.cs:172` | QueueStorage, EventGrid, ServiceBus (**flip**) **and** Kafka, EventHub (**carve-out**) | **Needs a policy hook — do NOT flip blanket.** See below |
| `src/Benzene.GoogleCloud.Functions.PubSub/PubSubMiddlewareApplication.cs:71` | Pub/Sub | Standalone flip |
| `src/Benzene.RabbitMq/RabbitMqWorker.cs:176` | RabbitMQ | Standalone flip (note: no `RaiseOnFailureStatus` prefix on this one) |
| `src/Benzene.Aws.Lambda.Kafka/KafkaApplication.cs:115` | AWS Lambda Kafka | **Carve-out — no change.** Already `== false` and already carries its rationale comment |

Before editing either base class, re-run this to confirm the consumer list has not moved again:

```
grep -rln "SingleContextEscalatingApplicationBase" src/ --include=*.cs
grep -rln "AzureFunctionBatchApplicationBase"      src/ --include=*.cs
```

`SingleContextEscalatingApplicationBase` must list only SNS, S3, EventBridge (plus its own file).
`SqsConsumerApplication` mentions it in a comment but does **not** extend it — do not be misled by a
`grep` that ignores the `--include` filter. If either list has changed, **stop and report**: a new
consumer means a policy row that §1 has not considered.

**The Azure base-class hook.** `AzureFunctionBatchApplicationBase` already has the right pattern for
this — `ShouldEscalateFailure(context, state)` (line 119, `virtual`, default `true`, overridden by
Service Bus to `!state.ExplicitAck`). Follow it exactly rather than inventing a new mechanism. Add a
second virtual alongside it:

```csharp
/// <summary>
/// Whether an unestablished outcome (no result recorded - typically an unrouted message no handler
/// matched) is escalated like a failure. True for queue-shaped transports, which have a DLQ /
/// poison / dequeue-count backstop to catch a retained message. Overridden to false by the stream
/// transports, which have no per-record dead-letter path: retaining an unrouted record there would
/// replay the partition from that offset forever. See work/settlement-consistency-fix-plan.md.
/// </summary>
protected virtual bool EscalateUnestablishedOutcome => true;
```

and make the guard consult it, leaving the `_raiseOnFailureStatus` and `ShouldEscalateFailure` terms
exactly as they are:

```csharp
var outcome = context.MessageResult?.IsSuccessful;
var shouldSettleAsFailure = EscalateUnestablishedOutcome ? outcome != true : outcome == false;

if (_raiseOnFailureStatus && shouldSettleAsFailure && ShouldEscalateFailure(context, state))
```

Then override it to `false` in the two carve-outs — `Benzene.Azure.Function.Kafka/KafkaApplication.cs`
and `Benzene.Azure.Function.EventHub/Function/EventHubApplication.cs` — each with a comment naming the
reason (no per-record DLQ / fan-in stream) and the consequence of removing the override (infinite
partition replay). **The override is the point of this design:** it turns a carve-out that previously
looked like an inconsistent `== false` into a named, declared policy that a future reader cannot
mistake for a bug and "tidy up".

**Also in this batch:**

- **`RabbitMqWorkerTest.NoResultRecorded_Acks`** — this test's expectation **changes**. Rename it to
  `NoResultRecorded_Nacks`, invert the assertion to `BasicNackAsync`, and rewrite its comment to state
  the new rule and the DLX backstop. This is the one place in this plan where an existing green test
  is deliberately inverted; it is expected, and it is why RabbitMQ carries its own decision line.
- Any other test asserting ack-on-null for rows 1–8 changes the same way. Find them with:
  `grep -rn "NoResult\|null result\|unrouted" test/ | grep -i "ack\|complete\|settle"`.
- Add a one-line comment at each flipped guard stating the rule and the backstop it relies on, in the
  register of the AWS Lambda Kafka carve-out comment (`KafkaApplication.cs:109-114`) — that comment is
  the house style for this: it states the rule, the reason, and the failure mode of getting it wrong.
- Update the per-package `CLAUDE.md` for each affected package where it describes null handling —
  including the two base-class packages, `Benzene.Aws.Lambda.Core` and `Benzene.Azure.Function.Core`,
  whose own `CLAUDE.md` files document the escalation contract.

**Verify:** full `Benzene.Core.Test` run green. Every failure must be one you deliberately inverted
above — an unexpected failure means the policy has hit something §1 did not anticipate, so **stop and
report** rather than adjusting either side. Pay particular attention to any Kafka or Event Hub test
that goes red: that means the base-class hook is not wired correctly and a carve-out has been flipped.

### Batch 2 — lock the policy in place so it cannot silently drift back

This is the batch that stops the next flip-flop, and it is the reason the rest is worth doing.

`test/Benzene.Core.Test/Contract/SettlementContractDefaultsTest.cs` already guards the **failure-result**
axis, pinning both the code defaults and the matching `docs/capability-matrix.md` row text. It has no
coverage of the **null** axis, which is exactly why Tier B could sit ambiguous for months.

Add a third axis to that file, following its existing style (it already reads files from disk, so a
source-level assertion is in keeping with the file's own conventions):

1. **`NullOutcomePolicy_MatchesTheDecidedTable`** — a table-driven test holding rows 1–18 of §1 as
   data: for each adapter, the file, and whether its guard must read `!= true` (retain) or
   `== false` (ack). Assert the guard's polarity in source. Include the carve-outs **as positive
   assertions** — the test must fail if someone "fixes" Kafka to retain-on-null.
2. **A completeness assertion** — enumerate the adapter application files and fail if one is not
   listed in the table above. This is what stops a *new* transport being added with an unconsidered
   settlement policy, which is how this drift started.
3. Point the test's header comment at this document by path, so the next person to touch it finds the
   policy rather than re-deriving it.

Where an adapter already has a `*FailureHandlingTest`, also add a behavioural null-outcome case
(pipeline records no result → assert escalate/nack). The source-scan is the backstop; a real
behavioural test is better evidence where the harness already exists.

**Verify:** the new tests pass; then deliberately revert one Batch 1 line locally, confirm the guard
test fails with a message that names this document, and restore it. A guard that does not actually
fail is worse than no guard.

### Batch 3 — document the fan-in contract (Tier C, rows 19–20)

Docs only, no code. In the package `CLAUDE.md` for `Benzene.Aws.Lambda.Kinesis` and
`Benzene.Azure.Function.Cosmos`, state plainly: a fan-in handler receives the whole batch and signals
failure by **throwing**, or (Kinesis) by withholding the checkpoint — **returning a failure result
does nothing**, because no per-record result is inspected. Add the same clarification wherever
`docs/cookbooks/cosmos-change-feed-processing.md` and the Kinesis material describe error handling.

**Verify:** docs build / link check; no code change, so no test movement expected.

### Batch 4 — reconcile the written record

The written record currently disagrees with itself, which is how an implementing agent gets misled.

- `work/settlement-default-alignment-proposal.md` — update the status header: Tier B **DECIDED
  2026-08-25** (retain-where-a-backstop-exists, per §1 here), Tier C **done** once Batch 3 lands.
  Its own instruction *"Do not archive this document while B and C are open"* is then satisfied, so
  move it to `work/archive/` and leave a one-line pointer to this document.
- **Do not edit `work/archive/settlement-contract-1.0-2026-07.md`.** It was archived in the
  2026-08-23 pass, and `work/archive/README.md` is explicit: *"Nothing here is current — do not cite
  any of it for status."* Dated records are never updated. **This document is the live home of both
  settlement axes from now on** — the failure axis it inherits, the null axis it decides.
- `work/outstanding-bugs.md` — move the two now-closed entries ("Tier B", "RabbitMQ null-result → ack")
  into its resolved half with the decision date, exactly as that file already does for decided items.
- `docs/capability-matrix.md` — the rows currently describe the failure axis only. Add the null-outcome
  behaviour per transport, since Batch 2's guard test asserts this text and will fail without it.
- `CHANGELOG.md` — a behavioural-change entry under `[Unreleased]`. **This is a breaking behavioural
  change**: an unrouted message that used to vanish now surfaces and is retried. Say so plainly, and
  give the one-line opt-out (`RaiseOnFailureStatus = false`) for anyone who relied on the old
  behaviour, in the register the archived settlement contract used for its own migration section.

**Verify:** Batch 2's capability-matrix assertions still pass against the edited text.

---

## 3. Not in scope for this plan — and why

These are on the backlog but must **not** be swept into the settlement work. Each is either a separate
decision or an unrelated risk profile; mixing them in is what makes a settlement change unrevertable.

| Item | Why it is out |
|---|---|
| **Kinesis / DynamoDB rely on `ReportBatchItemFailures` being configured on the event-source mapping** (`KinesisStreamApplication.cs:103`, `DynamoDbApplication.cs:66`) | Real gap — Benzene cannot see the ESM setting, so if it is off, a reported batch failure is silently a success. But the fix is a **new startup diagnostic or a thrown-exception fallback**, i.e. a design addition, not a polarity change. Worth doing; needs its own proposal. Documenting the dependency is a cheap interim step. |
| **`SchemaCompatibilityComparer` ignores enum/nullable/facets** | Needs new `SchemaChangeKind` values and a breaking-vs-warning classification per direction — a policy design task, not mechanical. |
| **`DynamoDbHealthCheck` ignores `TableStatus`** | Which statuses fail a health check is a policy call the maintainer has not made. |
| **`CachingHealthCheckProcessor` cache-key collision** (`:49`) | A genuine self-contained fix (two probes with the same type-set collide), unrelated to settlement. Good next task — separate branch. |
| **CR/LF response-header injection (defence-in-depth)** | Not a confirmed live vector; whether to strip centrally is a decision. Separate security-hardening task. |
| **`MiddlewareRouter` value-type constraint, Cosmos `MapChangeType`, `SnsMessageBodyGetter`** | Explicitly held for the 1.0 API freeze. **Do not touch.** |
| **All `[PERF]` items** | Safe and mechanical, but they touch the same hot paths; landing them in the same window makes a settlement regression harder to bisect. Do them after Batch 4 is green. |

---

## 4. Follow-up: promote the policy into the language-neutral spec

**Decided (maintainer, 2026-08-25): yes, but as a follow-up — not in this batch.**

Settlement policy currently lives only in this port. The spec's `docs/specification/transport-bindings.md` (in the **`benzene`** repo, not this one)
describes per-binding ack behaviour but sets **no cross-transport rule**, so Go, TypeScript and Python
can each land somewhere different — and then this same argument gets had three more times, with three
more flip-flops.

Once Batches 1–4 are green, raise a separate piece of work in the `benzene` repo to encode §1's two
axes and the backstop test as a spec-level rule, with conformance fixtures, so the other ports inherit
it rather than re-deciding it. Per `AGENTS.md` that is a spec change: it belongs in
`docs/specification/**` first, with the fixtures re-vendored here afterwards. **Do not start it as
part of this plan** — it widens the blast radius from one repo to five.

---

## 5. Decision register — what is decided, what is still open

Recorded so the next agent does not re-litigate any of it. **A decision here is closed. Reopening one
requires the maintainer, not a code review.**

| Decision | Status | Date |
|---|---|---|
| A returned failure result is not silently settled (`RaiseOnFailureStatus` defaults `true`) | **CLOSED** — `work/archive/settlement-contract-1.0-2026-07.md` | 2026-07-21 |
| Self-hosted *stream* workers stay at-most-once by default | **CLOSED** — halting a consumer on one poison record is too drastic a default | 2026-07-21 |
| Null/unrouted outcome: retain where a redelivery backstop exists | **CLOSED** — §1 above | 2026-08-25 |
| Kafka (×3) and fan-in streams keep ack-on-null | **CLOSED** — no per-record DLQ; retaining replays forever | 2026-08-25 |
| RabbitMQ ack-on-null reversed, overturning a documented+tested deliberate behaviour | **CLOSED**, but flagged: this reverses a written decision. If the maintainer disagrees, row 7 is the one to veto | 2026-08-25 |
| Pub/Sub and Service Bus Functions added to the flip list beyond the original proposal | **CLOSED** subject to maintainer veto before Batch 1 — see §1 † | 2026-08-25 |
| Promote settlement policy into the language-neutral spec | **CLOSED: yes, as a follow-up** — §4 | 2026-08-25 |
| Kinesis/DynamoDB ESM `ReportBatchItemFailures` dependency — warn, throw, or document only | **OPEN** — needs a proposal; documenting the dependency is the interim step | — |
| `SchemaCompatibilityComparer` breaking-vs-warning classification | **OPEN** | — |
| `DynamoDbHealthCheck` — which `TableStatus` values fail | **OPEN** | — |
| Central CR/LF header stripping | **OPEN** | — |

---

## 6. Related documents

- [`work/settlement-default-alignment-proposal.md`](settlement-default-alignment-proposal.md) — where Tier B came from; **still live** (its own text forbids archiving while B and C are open). Archive it after Batch 4
- [`work/outstanding-bugs.md`](outstanding-bugs.md) — the full backlog this plan draws from; **live**

**Archived — history and reasoning only. Per [`work/archive/README.md`](archive/README.md) these are
dated records: do not cite them for current status, and do not edit them.**

- [`work/archive/settlement-contract-1.0-2026-07.md`](archive/settlement-contract-1.0-2026-07.md) — the failure-result axis as decided 2026-07-21, and the migration/opt-out register this plan's changelog entry should echo
- [`work/archive/batch-failure-handling-2026-07.md`](archive/batch-failure-handling-2026-07.md), [`work/archive/kinesis-batch-failure-handling-design-2026-07.md`](archive/kinesis-batch-failure-handling-design-2026-07.md) — the fan-in/stream constraints behind rows 14–20
- [`docs/capability-matrix.md`](../docs/capability-matrix.md) — the user-facing statement of all of this
- `test/Benzene.Core.Test/Contract/SettlementContractDefaultsTest.cs` — the drift guard Batch 2 extends
