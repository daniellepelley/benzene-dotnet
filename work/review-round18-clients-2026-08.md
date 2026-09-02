# Round 18 review — outbound Clients family (egress/publishing)

Scope, per this round's brief: `Benzene.Clients` (the shared abstraction) plus every platform-specific
outbound client built on it — `Benzene.Clients.Http`, `.InProcess`, the AWS family
(`.Aws`/`.Aws.EventBridge`/`.Aws.Lambda`/`.Aws.Sns`/`.Aws.Sqs`/`.Aws.StepFunctions`), the Azure family
(`.Azure.EventGrid`/`.Azure.EventHub`/`.Azure.QueueStorage`/`.Azure.ServiceBus`),
`.GoogleCloud.PubSub`, and `.HealthChecks`. Specifically asked to focus on **correctness**, not the
already-tracked per-send converter allocation: partial-batch-failure semantics across clients,
retry/backoff interaction with `Benzene.Resilience`/`Benzene.Resilience.Polly`, and whether
`CancellationToken` actually aborts an in-flight send or only gates the initial call. Reviewed against
`main`/`7f642b2`. No dotnet SDK in this environment — every finding is traced by hand against the exact
source shown; no build/test was run. A regression test that would prove each finding is described
in-line rather than written to disk (per this round's constraints).

## Note on prior coverage

`work/outstanding-bugs.md`'s WP-C/#268/#261/#270 rounds already did a thorough, successful sweep
threading `ICancellationTokenAccessor` into every **single-send** AWS/Azure client and the AWS **batch**
clients (Sqs/Sns/EventBridge). That sweep's own scoping (by SDK family: "SQS, SNS, EventBridge, Lambda,
Step Functions, the three batch clients, and the standalone `Benzene.Aws.Sqs` `SqsMessageClient`", and
separately "Sns/Sqs/EventBridge/AwsLambda/EventGrid/EventHub/QueueStorage/ServiceBus") is exactly why the
two gaps below survived: they sit just outside each sweep's stated boundary — Google Cloud Pub/Sub was
never named in the single-send sweep, and none of the three Azure **batch** clients were named in the
batch sweep (only the three AWS ones were "the three batch clients").

## Finding 1 (headline) — `Benzene.Clients.GoogleCloud.PubSub`'s `PubSubClientMiddleware` never resolves
`ICancellationTokenAccessor`; it is the one outbound single-send transport WP-C's #268 sweep never
reached, so `.UseTimeout(...)`/host-shutdown cancellation around a Pub/Sub publish is a complete no-op

**Severity: high — a documented, general capability (ambient cancellation aborts an in-flight outbound
send) silently does not apply to this transport at all, with nothing in the constructor even to opt into
it.** Every other single-send outbound client in this family — `HttpClientMiddleware`,
`QueueStorageClientMiddleware`, `SqsClientMiddleware`, `SnsClientMiddleware`,
`EventBridgeClientMiddleware`, `AwsLambdaClientMiddleware`, `EventGridClientMiddleware`,
`EventHubClientMiddleware`, `ServiceBusClientMiddleware` — resolves an optional
`ICancellationTokenAccessor` and threads its `.CancellationToken` into the underlying SDK call
(confirmed directly, not just via `CLAUDE.md`, for `Http`/`QueueStorage` in this pass — see below).
`PubSubClientMiddleware` is the sole exception:

```csharp
// src/Benzene.Clients.GoogleCloud.PubSub/PubSubClientMiddleware.cs
public class PubSubClientMiddleware : IMiddleware<PubSubSendMessageContext>, ITerminalMiddleware
{
    private readonly PublisherServiceApiClient _publisher;

    public PubSubClientMiddleware(PublisherServiceApiClient publisher)
    {
        _publisher = publisher;
    }

    public async Task HandleAsync(PubSubSendMessageContext context, Func<Task> next)
    {
        var response = await _publisher.PublishAsync(context.TopicName, new[] { context.Message });
        context.MessageId = response.MessageIds.FirstOrDefault() ?? "";
    }
}
```

No `ICancellationTokenAccessor` field, no constructor parameter, no `CancellationToken` argument to
`PublishAsync` at all — not even an explicit `CancellationToken.None`. `PublisherServiceApiClient`
(Google's GAX-generated gRPC client) exposes a `PublishAsync(TopicName, IEnumerable<PubsubMessage>,
CancellationToken cancellationToken = default)` convenience overload, the same shape every other GAX
client in this codebase's Google Cloud ingress packages already relies on — the capability exists, it is
just never reached. `src/Benzene.Clients.GoogleCloud.PubSub/Extensions.cs`'s two `UsePubSubClient(...)`
overloads (DI-resolved and given-instance) confirm there is no other path that supplies one either —
neither resolves `ICancellationTokenAccessor` from the pipeline's service resolver the way
`HttpClientMiddleware`'s given-instance overload does (`#270`'s fix).

### Why this isn't the ingress fix already tracked

`work/outstanding-bugs.md` records a Pub/Sub cancellation fix (`test/Benzene.Core.Test/Google/
PubSubCancellationTest.cs`) — but that test lives under `test/Benzene.Core.Test/Google/` and targets
`Benzene.GoogleCloud.Functions.PubSub`'s `PubSubMiddlewareApplication`/`GooglePubSubFunctionHost` — the
**ingress** side (the Cloud Functions Framework's own invocation token reaching a handler). Confirmed
directly: that test file's `using` list has no `Benzene.Clients.GoogleCloud.PubSub` reference at all, and
no test file anywhere under `test/` references `PubSubClientMiddleware`
(`grep -rl PubSubClientMiddleware test/` finds nothing). The **egress** client this finding is about was
never in scope for that fix, and #268's own file list (`Sns/Sqs/EventBridge/AwsLambda/EventGrid/EventHub/
QueueStorage/ServiceBus`) never named it either — eight transports were fixed, Pub/Sub egress was simply
never enumerated.

### Concrete failure scenario

A service wraps its Pub/Sub publish route in `.UseTimeout(TimeSpan.FromSeconds(2))` (the same pattern
every other transport's `CLAUDE.md`/tests exercise), expecting a stuck publish to abort at 2s so the
caller gets a prompt failure instead of hanging until the gRPC channel's own default deadline. Because
`PubSubClientMiddleware` never observes any token, the outer timeout's cancellation is signalled but
nothing downstream is listening for it — the `PublishAsync` call runs to whatever the gRPC
channel/library's own default timeout is (which can be tens of seconds to indefinite, depending on
channel configuration), not the 2s the caller configured. The same applies to host-shutdown /
graceful-drain cancellation: a Pub/Sub publish in flight when the host starts a graceful stop is not
told to abort, unlike a same-shaped SQS/SNS/Service Bus/Event Hub send in the identical position.

### Regression test that would prove it

Mirror `test/Benzene.Core.Test/Clients/Azure/EventHub/EventHubClientMiddlewareCancellationTest.cs`'s
shape: a mocked `PublisherServiceApiClient` (it's Google's own non-sealed GAX client type, already
noted as directly Moq-mockable in this package's own `CLAUDE.md`) whose `PublishAsync` setup captures
the `CancellationToken` argument it was actually called with; drive `PubSubClientMiddleware.HandleAsync`
through a pipeline seeded with a real, non-default token via `ICancellationTokenAccessor`, and assert the
captured token is that exact instance, not `default`/`CancellationToken.None`. Today there is no such
test anywhere in the repo for this specific class, which is exactly why the gap survived the #268 sweep.

### Recommendation

Give `PubSubClientMiddleware` the same constructor-optional `ICancellationTokenAccessor? cancellation =
null` idiom every sibling uses, thread `_cancellation?.CancellationToken ?? CancellationToken.None` into
`PublishAsync`, and wire it through both `UsePubSubClient()` overloads (the DI-resolved one via
constructor injection, the given-instance one via `serviceResolver.TryGetService<ICancellationTokenAccessor>()`,
matching `HttpClientMiddleware`'s given-instance fix in #270). Purely additive — no interface or wire
change, same shape as the eight `#268` fixes.

---

## Finding 2 (headline) — `ServiceBusBatchMessageClient`/`EventHubBatchMessageClient`'s own native-batch
creation calls (`CreateMessageBatchAsync`/`CreateBatchAsync`) are not inside any `catch`, so a transient
failure creating a batch throws straight out of `SendBatchAsync`, discarding every already-recorded
per-entry result and breaking the "never throws, always returns `BatchSendResult`" contract every other
batch client in the family honors

**Severity: high — this is exactly the "publish 5 messages, message 3 fails, what happens to 1-2 and
4-5" question the brief asks, and for these two clients the answer is "the caller gets neither an
acknowledgement for 1-2 nor an index for what to retry — just an unhandled exception."**
`Benzene.Clients/CLAUDE.md`'s own batch-send section is explicit about the contract every
`IBenzeneBatchMessageClient` implementation is meant to honor: "`Failures` names exactly the entries the
provider rejected" for AWS, and for the atomic Azure primitives "a failed batch/chunk send reports
*every* message in that batch as failed" — in both cases the caller gets a `BatchSendResult` back, never
an exception, so "the caller can retry exactly the failed subset." Every batch client's own doc comment
and in-line comments restate this ("matching the AWS batch clients' per-entry contract... rather than
aborting the whole batch after earlier chunks already sent"). `ServiceBusBatchMessageClient` and
`EventHubBatchMessageClient` both honor this for the actual *send* call
(`SendMessagesAsync`/`SendAsync`, wrapped in `SendBatchAndTrackFailuresAsync`'s own try/catch) — but not
for the call that creates the native batch object in the first place, which both packages call **twice**:
once before any `try` at all, and once more, mid-loop, inside a `try` block that has a `finally` but no
`catch`.

### The defect — `ServiceBusBatchMessageClient.cs`

```csharp
// src/Benzene.Clients.Azure.ServiceBus/ServiceBusBatchMessageClient.cs, SendBatchAsync
ServiceBusMessageBatch? batch = await _sender.CreateMessageBatchAsync();   // line 59 — OUTSIDE any try
var batchIndices = new List<int>();
var index = 0;

try                                                                        // line 63
{
    foreach (var request in requests)
    {
        ...
        if (!batch.TryAddMessage(message))
        {
            ...
            await SendBatchAndTrackFailuresAsync(batch, batchIndices, failures);
            batch.Dispose();
            batch = null;

            batch = await _sender.CreateMessageBatchAsync();               // line 99 — inside the
            batchIndices = new List<int>();                                 // try, but the try/finally
                                                                              // below has NO catch
            ...
        }
        ...
    }
    ...
}
finally
{
    batch?.Dispose();                                                       // lines 120-123 — only
}                                                                            // disposal, no recovery

return new BatchSendResult(failures);
```

`ServiceBusSender.CreateMessageBatchAsync()` is not a pure local allocation — on first use (or after the
entity's negotiated max-message-size is invalidated) it round-trips to the Service Bus service to learn
`MaxMessageSizeInBytes`, so it is a genuine network call that can throw (`ServiceBusException` on a
transient broker/network hiccup, exactly the class of failure `SendBatchAndTrackFailuresAsync`'s own
catch already anticipates for the send call two lines away). If it throws:
- **On line 59** (the very first batch of the whole `SendBatchAsync` call, before `requests` has even
  started iterating): the exception propagates straight out of `SendBatchAsync` — `failures` (still
  empty at this point) is never returned; the caller gets a raw `ServiceBusException`, not a
  `BatchSendResult`.
- **On line 99** (mid-loop, rolling to a fresh batch after an earlier one filled and was already sent
  successfully): `batch` was just set to `null` on the line above, so `finally { batch?.Dispose(); }` is
  a harmless no-op — but the exception still propagates out of `SendBatchAsync`, discarding every
  `FailedBatchEntry` already accumulated in `failures` for earlier chunks **and every already-`Send`ed
  successful chunk's implicit acknowledgement** — the caller has no way to learn which of the messages
  sent so far actually succeeded before the exception, so a safe retry means re-sending the *entire*
  original `requests` collection, re-delivering everything that already went through.

### The identical defect — `EventHubBatchMessageClient.cs`

```csharp
// src/Benzene.Clients.Azure.EventHub/EventHubBatchMessageClient.cs, SendGroupAsync
EventDataBatch? batch = await _producerClient.CreateBatchAsync(batchOptions);   // line 104 — OUTSIDE try
var batchIndices = new List<int>();

try                                                                              // line 107
{
    foreach (var (context, itemIndex) in group)
    {
        if (!batch.TryAdd(context.EventData))
        {
            ...
            await SendBatchAndTrackFailuresAsync(batch, batchIndices, failures);
            batch.Dispose();
            batch = null;

            batch = await _producerClient.CreateBatchAsync(batchOptions);       // line 124 — inside the
            batchIndices = new List<int>();                                      // try, no catch either
            ...
        }
        ...
    }
    ...
}
finally
{
    batch?.Dispose();                                                           // lines 143-146
}
```

Same shape, same two unguarded call sites (`SendGroupAsync` is called once per resolved partition-key
group from `SendBatchAsync`, so an exception here escapes `SendGroupAsync`, then `SendBatchAsync`
itself, uncaught). `EventHubProducerClient.CreateBatchAsync` is likewise a network call (it may need to
learn partition/entity properties), not a pure local allocation.

### Contrast with the rest of the family — this is not how the other four batch clients behave

- `SqsBatchMessageClient`/`SnsBatchMessageClient`/`EventBridgeBatchMessageClient` (AWS): every SDK call
  is inside a `try/catch` that feeds `failures`; nothing AWS-related is ever called outside one.
- `EventGridBatchMessageClient` (Azure): builds a plain `List<CloudEvent>` per chunk (no native
  "create a batch object" SDK call at all) and calls `SendEventsAsync` inside a `try/catch`. It has no
  analogous unguarded call, so it is **not** affected by this finding.

So this is specific to the two Azure clients that use the "native batch object, roll to a new one when
full" pattern (`ServiceBusBatchMessageClient`, `EventHubBatchMessageClient`) — the two-per-family that
happen to need an extra "create the batch" round trip the other four don't.

### Concrete failure scenario

A caller sends 25 messages via `ServiceBusBatchMessageClient.SendBatchAsync` against a Service Bus
namespace under transient load. The first `CreateMessageBatchAsync()` succeeds and the first ~15 fit in
one batch, which sends successfully. The batch is disposed and a second `CreateMessageBatchAsync()` is
issued to hold the remaining ~10 — this one hits a transient `ServiceBusException` (throttling, a
momentary AMQP link recycle). The exception now propagates out of `SendBatchAsync` entirely: the caller
gets an unhandled `ServiceBusException`, not a `BatchSendResult` reporting 15 succeeded and 10 failed
with their indices. If the caller's retry logic (reasonably, per this package's own documented contract)
assumes `SendBatchAsync` either returns a `BatchSendResult` or the whole call failed outright, it will
re-send **all 25** messages on retry — re-delivering the 15 that already went through, silently
violating the "retry exactly the failed subset" promise the whole `BatchSendResult` design exists to
keep.

### Regression test that would prove it

Extend `test/Benzene.Core.Test/Clients/Azure/BatchMessageClientTest.cs`'s existing Moq-based
`ServiceBusSender`/`EventHubProducerClient` doubles: set up `CreateMessageBatchAsync`/`CreateBatchAsync`
to succeed on its first call (so a first batch sends successfully) and throw (a
`ServiceBusException`/`EventHubsException` mimicking a transient failure) on the **second** call — i.e.
enough messages to require a roll. Assert `SendBatchAsync` either does not throw (and instead returns a
`BatchSendResult` whose `Failures` names the not-yet-sent entries while treating the first batch's
entries as succeeded) or, at minimum, that whatever it does throw carries the already-accumulated
partial result rather than discarding it silently. Today's suite (`BatchMessageClientTest.cs`,
`BatchEdgeCaseProbeTest.cs`) only ever fails the **send** call (`SendMessagesAsync`/`SendAsync`), never
the batch-creation call, which is exactly why this gap survived.

### Recommendation

Wrap both `CreateMessageBatchAsync`/`CreateBatchAsync` call sites (the initial one and the mid-loop roll)
in the same kind of try/catch `SendBatchAndTrackFailuresAsync` already applies to the send call, mapping
a thrown exception there onto `FailedBatchEntry` records for whatever indices haven't been reported yet
(the current chunk's remaining, not-yet-batched entries) — mirroring how the AWS clients treat "the SDK
call for this unit of work threw" uniformly regardless of which SDK call it was. This keeps
`SendBatchAsync`'s contract ("returns a `BatchSendResult`, does not throw for a transport-level failure")
consistent across all six batch clients in the family, and preserves already-accumulated `failures` (and
the implicit successes of earlier, already-sent batches) instead of discarding them on an exception path
nothing else in this file expects to reach the caller.

---

## Other areas swept — no additional finding clearing the bar

- **`Benzene.Clients` core (`OutboundRoutingBuilder`, `DefaultBenzeneMessageSender`,
  `ParallelOutboundMiddleware`, `ValidateOutboundRoutingExtensions`)** — re-read against this round's
  brief specifically for partial-fan-out and cancellation-classification behavior. `#269`'s
  `BranchOutcome.IsCancelled` handling in `ParallelOutboundMiddleware` (round 14-15) already gives
  ambient-cancellation-during-fan-out its own distinct, correctly-classified outcome, and every branch
  still runs to completion regardless of a sibling's cancellation/failure — this is deliberate,
  documented, and still correct on a fresh read. No new finding.
- **AWS batch clients (`Sqs`/`Sns`/`EventBridge`)** — re-verified the partial-failure design end to end:
  a per-entry conversion failure, a per-chunk transport failure, and a genuine provider-reported
  per-entry failure are all correctly folded into `BatchSendResult.Failures` against the caller's
  original indices, and a chunk-level exception never discards earlier chunks' already-recorded results
  (unlike Finding 2 above) because the chunking loop has no equivalent "unguarded native batch object"
  call — `BatchSend.Chunk` is a pure in-memory index-pairing helper, not an SDK round trip. Clean.
- **`EventGridBatchMessageClient`** — same check as above; no unguarded SDK call exists (no native batch
  object to create), so Finding 2's defect class does not apply here. Clean.
- **`Benzene.Clients.InProcess.InProcessFanOutClientMiddleware`** — the in-process analogue of "publish
  to several targets, one fails": each target's failure (thrown exception or non-success status) is
  isolated via its own `try/catch` inside `DispatchAsync`, logged, and does not affect the other targets
  or the caller's always-`Ok<Void>` response — a deliberate, documented "no in-process DLQ" design (see
  its own `CLAUDE.md`), not a bug. Cancellation is not threaded into the dispatched
  `IMiddlewareApplication.HandleAsync` call, but this is in-process dispatch with no I/O to abort, and is
  consistent with the package's stated scope; not flagged.
- **`Benzene.Clients.Aws.StepFunctions.StepFunctionsClient`** — re-checked the idempotent-retry path
  (`ExecutionAlreadyExistsException` → `DescribeExecution` byte-for-byte input comparison) specifically
  for a cancellation-during-ambiguous-retry scenario: if the first `StartExecutionAsync` call is
  cancelled by an outer deadline after the execution genuinely started server-side, the caught exception
  is an `OperationCanceledException`, not `ExecutionAlreadyExistsException`, so this call reports
  `ServiceUnavailable` rather than a false `Accepted` — but a subsequent retry with the same
  `executionName` correctly discovers the already-started execution and, if the input matches, reports
  `Accepted` (a true idempotent recovery). This is correct, deliberate design, already documented; not a
  new finding. `#261`'s cancellation threading (`StartExecutionAsync`/`DescribeExecutionAsync` both take
  the ambient token) works as intended.
- **`Benzene.Clients.HealthChecks` (`ServiceHealthCheckClient`, `ClientHealthCheckProcessor`)** — no
  network I/O of its own; sends through the consumer's own registered `IBenzeneMessageSender` outbound
  route, so it inherits whatever cancellation/retry behavior that route's own transport middleware
  provides (Findings 1-2 above and the already-fixed #268/#261 family). `ClientHealthCheckProcessor` is a
  pure in-memory comparison step. No finding.
- **SDK-level retry vs. outer `Benzene.Resilience`/`.Polly` retry "double-delivery" risk (asked for
  explicitly in the brief)** — traced this deliberately. Every outbound client in this family takes an
  already-constructed SDK client (`IAmazonSQS`, `ServiceBusSender`, `PublisherServiceApiClient`, …) from
  the caller; Benzene itself never constructs one with baked-in retry configuration
  (`grep -rn "RetryMode|MaxErrorRetry|RetryPolicy" src/Benzene.Clients.*` returns nothing outside dev/test
  factories like `LocalAwsLambdaClientFactory`). `#261`'s own fix explicitly noted it "does NOT change
  SDK retry/timeout configuration — only threads the ambient token." So a caller stacking
  `.UseRetry(...)`/Polly around a client whose underlying SDK object also retries under the hood can
  indeed end up with retry-of-a-retry — but that is a consequence of how the *caller* constructs and
  configures their own SDK client, not a Benzene code defect; nothing in this family silently duplicates
  a retry on its own. Not filed as a bug; flagging here only so a future reviewer doesn't need to
  re-derive that this is a caller-configuration concern, not a code-level one.
- **Batch-client cancellation catch granularity (minor, not filed)** — the AWS batch clients' chunk-level
  `catch (Exception ex)` folds a genuine `OperationCanceledException`/`TaskCanceledException` into the
  same generic `FailedBatchEntry(index, ex.GetType().Name, ex.Message)` shape as any other transport
  failure, unlike `ParallelOutboundMiddleware`'s `#269` fix, which gives an ambient-cancellation-driven
  branch failure its own distinct `"{branch}: Cancelled"` classification. The practical outcome (the
  entries land in `Failures`, safe to retry) is unaffected either way — this is a UX/consistency
  observation about *how* the failure is labeled, not a functional bug, so it does not clear this
  round's bar for a filed finding.

## Summary

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | `Benzene.Clients.GoogleCloud.PubSub.PubSubClientMiddleware` never resolves `ICancellationTokenAccessor` — the one single-send outbound transport WP-C's #268 sweep never reached — so ambient cancellation/timeout has zero effect on a Pub/Sub publish | High — reliability/cancellation | New, traced by hand (no dotnet SDK available) |
| 2 | `ServiceBusBatchMessageClient`/`EventHubBatchMessageClient`'s `CreateMessageBatchAsync`/`CreateBatchAsync` calls are not wrapped in any `catch`, so a transient failure creating a native batch throws straight out of `SendBatchAsync`, discarding every already-accumulated `BatchSendResult` entry and violating the "never throws, returns per-entry failures" contract every other batch client in the family honors | High — reliability/data-loss-on-retry | New, traced by hand (no dotnet SDK available) |

Both findings sit precisely at the seams of prior rounds' own scoping (a transport-family sweep that
enumerated eight of nine single-send transports; a batch-client sweep that only ever named "the three
[AWS] batch clients") — consistent with this review series' established pattern that fresh code is
lower-yield than the boundary of an otherwise-thorough prior sweep. Every other area in this territory —
the AWS batch clients' partial-failure semantics, `ParallelOutboundMiddleware`'s fan-out semantics,
`InProcessFanOutClientMiddleware`, `StepFunctionsClient`'s idempotent-retry path, and the
`Benzene.Clients.HealthChecks` package — was genuinely swept and found correct against a real
failure-scenario trace, not skipped to pad the finding count.

**Recommendation: REQUEST CHANGES** on both findings; both are concrete, each with a described
regression test, and both are narrow, additive fixes (thread an existing idiom into one more class;
wrap two more SDK calls in the same try/catch pattern already used two lines away in the same file) —
neither requires a design decision the way several open items in `work/outstanding-bugs.md`'s
maintainer-decision section do.
