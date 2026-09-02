# Round 18 - Google Cloud, RabbitMQ, and Kafka Transports Review (2026-08-30/31)

**Scope, per the brief:** `Benzene.GoogleCloud.Functions.Core`/`.Http`/`.PubSub`,
`Benzene.Clients.GoogleCloud.PubSub`, `Benzene.Mesh.GoogleCloud.Storage`, `Benzene.RabbitMq`,
`Benzene.Kafka.Core`, `Benzene.Aws.Lambda.Kafka`, `Benzene.Azure.Function.Kafka`, and their
`*.TestHelpers` siblings - reviewed at commit `7f642b2` on `main`. The brief called this territory a
"first-ever pass" in places; treated accordingly - read every production file in scope end to end
(not just what round 10/17 already fixed), each package's own `CLAUDE.md` first for intent, then the
code against it, then `work/outstanding-bugs.md` to avoid re-litigating settled ground.

**Method / environment note:** no .NET SDK is available in this environment - nothing here was built
or executed. Every finding below is a manual trace against the source, cross-checked against
documented RabbitMQ.Client / Confluent.Kafka / Google GAX client behavior (AMQP 0-9-1 delivery-tag
scoping, RabbitMQ.Client's connection-scoped automatic-recovery model, GAX's standard
CancellationToken-overload convention) rather than confirmed by running a test. Each finding below
names the regression test that would prove it for a future round with CI access, per the task's
constraints - none of those tests were written or run.

Two findings cleared the bar as genuine, provable-by-tracing bugs; a third is a smaller, real
one-line-fix-shaped gap. Two large areas (`Benzene.Kafka.Core`'s rebalance/offset-commit machinery,
`Benzene.Aws.Lambda.Kafka`'s partition ordering) turned out to already be exactly as careful as their
`CLAUDE.md`s claim - rounds 10/17 already put this territory's highest-value Kafka findings to bed.

---

## Worth-fixing

### 1. [HIGH] `RabbitMqWorker` has no channel-recovery or channel-health detection at all - a handler in flight across a connection drop can permanently kill the consuming channel, and the worker never notices

`src/Benzene.RabbitMq/RabbitMqWorker.cs:66-141` (`StartAsync`/`StopAsync`), `202-240`
(`AckAsync`/`NackAsync`). Contrast with `src/Benzene.RabbitMq/RabbitMqSendMessage/RabbitMqMandatoryPublishCoordinator.cs:307-321`
(`OnChannelShutdownAsync`), which solves the *outbound* side of exactly this problem and was
deliberately built for it (task board #30/#45, per that file's own remarks) - the inbound worker has
no analogous handler.

**The mechanism.** `RabbitMqConnectionFactory`'s own doc comment (`src/Benzene.RabbitMq/RabbitMqConnectionFactory.cs:6-9`)
states: "Automatic recovery is on by `ConnectionFactory`'s own default, so a dropped
connection/channel is transparently re-established." That is true for *future* consume/publish
operations on the recovered channel object (the same C# `IChannel` reference survives recovery), but
it elides a real, well-documented AMQP/RabbitMQ.Client boundary: **delivery tags are scoped to one
channel-open lifetime and do not survive a recovery.** When the connection drops and
`AutomaticRecoveryEnabled` (RabbitMQ.Client's own default) reconnects it, the recovered channel is a
genuinely new AMQP channel underneath the same wrapper object, and its delivery-tag counter restarts
from 1. Any delivery that was mid-handling at the moment of the drop keeps its *old* `DeliveryTag` in
the `BasicDeliverEventArgs` the worker copied at `RabbitMqWorker.cs:147-155` and is still holding in
`HandleDeliveryAsync` (`RabbitMqWorker.cs:164-200`) - that number now refers to nothing (or, worse,
to a *different*, newly-delivered message that happens to have been assigned the same low tag after
recovery).

When that in-flight handler finishes and the worker calls `channel.BasicAckAsync(delivery.DeliveryTag, false)`
(`AckAsync`, `RabbitMqWorker.cs:212`) or `BasicNackAsync` (`NackAsync`, `RabbitMqWorker.cs:234`) with
the stale tag, RabbitMQ's own protocol handling of an ack/nack for an unknown delivery tag is a
channel-level protocol violation (`PRECONDITION_FAILED`) that the broker closes the channel over -
this is standard, documented AMQP 0-9-1 behavior distinct from a connection-level drop, and it is
**not** something RabbitMQ.Client's automatic recovery repairs: automatic recovery is scoped to
*connection* shutdown, not an independently-closed channel while the connection is still healthy.
Both `AckAsync` and `NackAsync` swallow this into a generic `catch (Exception ex)` →
`_logger.LogError(...)` (`RabbitMqWorker.cs:214-217`, `236-239`) and continue as if nothing happened.

**The consequence.** Once that channel is closed at the protocol level:
- The `AsyncEventingBasicConsumer` registered on it (`RabbitMqWorker.cs:87-90`) is dead - `_channel`
  never fires `ReceivedAsync` again. No new deliveries ever reach `OnReceivedAsync`
  (`RabbitMqWorker.cs:143-162`) again.
- The connection itself (`_connection`) stays open (a channel-level close is not a connection-level
  event), so nothing in `RabbitMqWorker` observes a failure - there is no unhandled exception, no
  faulted task, no log line beyond the one `LogError` for the ack/nack that triggered it. The process
  keeps running, healthy-looking.
- `RabbitMqWorker` never subscribes to `_channel.ChannelShutdownAsync` (or any connection-recovery
  event) to detect this and re-open a fresh channel/consumer - unlike its own sibling,
  `RabbitMqMandatoryPublishCoordinator`, which subscribes to exactly that event
  (`RabbitMqMandatoryPublishCoordinator.cs:109`, handler at `307-321`) specifically so a dead channel
  doesn't strand its callers. The consuming side has no equivalent.
- Any other deliveries that were still unacked on that now-closed channel are released back to the
  broker (a channel close implicitly requeues its own unacked deliveries) - but with no live consumer
  left on the queue (this being a single-worker deployment, the common case for a self-hosted
  process), they simply sit unconsumed. **This is precisely the "does it recover cleanly or silently
  stop consuming?" failure mode the round's brief calls out** - here it silently stops.
- Compounding it: `RabbitMqHealthCheck` (via `RabbitMqConnectionProvider`) deliberately uses a
  **separate** connection from the worker's own consuming connection (documented as intentional in
  `src/Benzene.RabbitMq/CLAUDE.md`'s health-check section: "It does **not** share the worker's
  private connection"). That connection is unaffected by the dead consuming channel, so the health
  check keeps reporting healthy while the actual consumer has gone silent.

**Why this is a real, not merely theoretical, scenario.** It requires no more than an in-flight
handler (`ConcurrentRequests` defaults to 5, so up to 5 can be in flight at once) surviving past a
connection blip - any handler that calls out to a database/HTTP dependency with real latency is
routinely still running when a multi-second network hiccup or broker restart occurs and recovers.
`AckAsync`/`NackAsync` are then called against a channel whose identity (reference) is unchanged but
whose delivery-tag numbering has silently reset underneath it.

**Suggested fix shape:** subscribe to `_channel.ChannelShutdownAsync` in `StartAsync` (mirroring
`RabbitMqMandatoryPublishCoordinator.OnChannelShutdownAsync`'s registration) and, on an
*unexpected* shutdown (one not caused by `StopAsync`'s own graceful `BasicCancelAsync`/`CloseAsync`),
either (a) proactively reopen a fresh channel + consumer against the still-alive (or since-recovered)
connection and resume consuming, or (b) treat it as a fatal worker fault - log at `Critical` and stop
the worker via the same signal `BenzeneKafkaWorker`'s `onFault`/`CatchHandlerExceptions=false` path
uses, so an orchestrator (Kubernetes, a process supervisor) restarts the process instead of it idling
silently. Either is preferable to the current "no detection at all" state. Separately, consider
whether `AckAsync`/`NackAsync`'s swallowed exception should distinguish "channel already closed" from
a merely transient ack failure, so the log at minimum flags the more serious condition distinctly.

**Regression-test shape for a future round with CI:** against a mocked `IChannel` (the existing
`RabbitMqWorkerTest` pattern, which already drives real deliveries through the
`AsyncEventingBasicConsumer`), have `BasicAckAsync` throw (simulating the broker's `PRECONDITION_FAILED`
channel close for a stale tag) and have the mock's `IsOpen` flip to `false` immediately after,
mirroring what a real closed channel would do. Assert that the worker takes some corrective action
(reopens a channel and keeps consuming, or signals a fault) rather than merely logging and continuing
to sit on a channel whose `IsOpen` is now `false` with no further action taken. Today, no such
assertion can be written to pass - `StopAsync`/`Dispose` are the only paths that ever touch `_channel`
again after `StartAsync` returns.

---

### 2. [MEDIUM] `PubSubClientMiddleware` never threads the ambient cancellation token into the outbound Pub/Sub publish call - the exact bug class `#236`/`#237` already fixed for RabbitMQ's and Kafka's outbound client middleware, left unfixed on this third sibling

`src/Benzene.Clients.GoogleCloud.PubSub/PubSubClientMiddleware.cs:23-41`,
`src/Benzene.Clients.GoogleCloud.PubSub/Extensions.cs:20-37`.

`RabbitMqClientMiddleware` (`src/Benzene.RabbitMq/RabbitMqSendMessage/RabbitMqClientMiddleware.cs:19,
45-50, 55-69, 92`) and `KafkaClientMiddleware` (per `src/Benzene.Kafka.Core/CLAUDE.md`'s own #237
entry) both take an optional `ICancellationTokenAccessor? cancellation` constructor parameter,
resolved from DI (or explicitly, for the given-instance overload), and thread
`cancellation?.CancellationToken ?? CancellationToken.None` into the actual broker SDK call - this
was a deliberate, documented fix (#236 RabbitMQ, #237 Kafka) for the exact problem of a
`.UseTimeout(...)`-wrapped or host-cancelled outbound send being unable to actually cancel/bound the
underlying I/O, previously always passing `CancellationToken.None` regardless of the pipeline's own
cancellation signal.

`PubSubClientMiddleware` has neither:

```csharp
public async Task HandleAsync(PubSubSendMessageContext context, Func<Task> next)
{
    var response = await _publisher.PublishAsync(context.TopicName, new[] { context.Message });
    context.MessageId = response.MessageIds.FirstOrDefault() ?? "";
}
```

No `ICancellationTokenAccessor` is resolved anywhere in `Benzene.Clients.GoogleCloud.PubSub` (`grep`
across the package returns zero hits for `CancellationToken`/`ICancellationTokenAccessor`), and
`_publisher.PublishAsync(...)` is called with no cancellation argument at all. `PublisherServiceApiClient`
is a GAX-generated client (Google.Cloud.PubSub.V1 3.36.0 per the `.csproj`); GAX-generated .NET
clients uniformly generate a `CancellationToken` convenience overload alongside the `CallSettings`
one for every RPC method, so the fix is mechanical and mirrors the two already-shipped siblings.

**Concrete failure scenario:** an outbound route published via `.UsePubSub(topic)` and wrapped in
`.UseTimeout(...)` (or any host-shutdown-driven cancellation), against a Pub/Sub endpoint that is slow
or unresponsive (a regional outage, a throttled project), cannot actually be cancelled/bounded by that
timeout - the gRPC call to `PublisherServiceApiClient.PublishAsync` runs to whatever its own internal
default deadline is (GAX's default RPC timeout, not the caller's), holding the outbound pipeline (and
whatever awaited it) open for far longer than the caller asked for. This is the same class of bug
already fixed twice in this codebase, just not carried to this third outbound client package.

**Suggested fix shape:** add a constructor-optional `ICancellationTokenAccessor? cancellation = null`
parameter to `PubSubClientMiddleware`, thread `cancellation?.CancellationToken ?? CancellationToken.None`
into `_publisher.PublishAsync(context.TopicName, new[] { context.Message }, cancellationToken)`, and
wire it through both `UsePubSubClient` overloads in `Extensions.cs` (the DI-resolved overload picks it
up via constructor injection since `Benzene.Core.MessageHandlers` registers it; the given-publisher
overload needs to resolve it explicitly from the pipeline's service resolver, mirroring
`RabbitMqClientMiddleware`'s given-channel overload).

**Regression-test shape:** mirroring `RabbitMqClientMiddlewareCancellationTest`/
`KafkaClientMiddlewareCancellationTest` - construct `PubSubClientMiddleware` with a mocked
`PublisherServiceApiClient` and a fake `ICancellationTokenAccessor` returning a specific, distinguishable
token; assert that exact token (not `It.IsAny<CancellationToken>()`) reaches the mocked `PublishAsync`
call. Today the call site has no cancellation parameter to assert on at all.

---

### 3. [LOW] `RabbitMqConnectionProvider`/`RabbitMqHealthCheck` and the worker's own consuming connection are fully independent - already a documented tradeoff, but worth restating given finding #1

Not a new finding on its own (the package's own `CLAUDE.md` documents this as "a deliberate
simplification"), but finding #1 above sharpens its cost: a health check that cannot see the specific
failure mode most likely to actually take a RabbitMQ consumer down silently (a dead, protocol-closed
consuming channel with the connection still open) provides less safety-net value than its "verifies
the broker is reachable and the consumed queue exists" framing suggests to an operator relying on it
for liveness. Left as a note rather than a separate finding since the maintainers already made this
tradeoff deliberately and documented it; flagging only because #1 changes how much that tradeoff
actually costs. No action item beyond what #1 already proposes.

---

## Reviewed, no finding

- **`Benzene.Kafka.Core`'s rebalance handling** (`BenzeneKafkaWorker.OnPartitionsRevoked`/
  `OnPartitionsLost`, `src/Benzene.Kafka.Core/BenzeneKafkaWorker.cs:390-480`) - traced the specific
  scenario the brief flagged (a partial drain timeout during a revoke, followed by `consumer.Commit()`)
  and it holds up: `Commit()` with no arguments only ever commits *stored* offsets, and `StoreOffset`
  is only ever called after a record's handler has genuinely finished (`BuildHandle`,
  `BenzeneKafkaWorker.cs:230-292`), so a commit firing while some other, non-revoked partition's record
  is still in flight can't over-commit past what's actually done - it just commits whatever safe
  watermark already existed. The revoked/lost handler split (#118) and its reasoning (a lost partition
  is likely already reassigned, so committing there would race the broker's own generation fencing)
  re-verified correct on read. `CommitOnlyOnSuccess`'s `EnableAutoOffsetStore=false` + the
  `PreserveOrderPerPartition=true`/`CatchHandlerExceptions=false` startup guards (`StartAsync`,
  `BenzeneKafkaWorker.cs:68-111`) all still hold.
- **`Benzene.Kafka.Core`'s offset-commit correctness under `RaiseOnFailureStatus`** - `HandleRecordAsync`
  (`BenzeneKafkaWorker.cs:302-311`) correctly escalates a non-throwing failure result onto the same
  `StoreOffset`-withholding path as a thrown exception; the one carve-out (default auto-store, where
  librdkafka already stored the offset before the handler ran) is honestly logged as unactionable
  rather than pretending to escalate something nothing can retry. Matches the package's own doc
  comment's claims exactly.
- **`Benzene.Aws.Lambda.Kafka`'s per-partition ordering + partial-batch-failure reporting**
  (`src/Benzene.Aws.Lambda.Kafka/KafkaApplication.cs`) - `ProcessPartitionAsync` explicitly
  `OrderBy(r => r.Offset)`s before processing (so an out-of-order `KafkaEvent.Records` dictionary
  value list, if AWS ever sent one, wouldn't break the ordering contract), stops at the first failure
  per partition, and correctly distinguishes an unrouted record (skip, not reported - Kafka has no
  per-record DLQ) from a genuine failure (reported, resume point named). The `itemIdentifier` JSON
  *object* shape (`{partition, offset}`, not a bare string like Kinesis/SQS) is correctly produced.
- **`Benzene.Azure.Function.Kafka`** - the documented ack-on-null carve-out
  (`KafkaBatchApplication.EscalateUnestablishedOutcome => false`) and `RaiseOnFailureStatus`
  safe-by-default posture both re-verified against the code as described; no discrepancy between the
  `CLAUDE.md`'s claims and `KafkaApplication`/`KafkaOptions`' actual behavior found.
- **`Benzene.GoogleCloud.Functions.Core`/`.Http`** - both are thin, correctly so; `GoogleCloudFunctionHost`'s
  constructor mirrors `AwsLambdaHost<TStartUp>` exactly, and `GoogleCloudFunctionApplicationBuilder`'s
  deferred-build (`Add`/`Build`) correctly throws `InvalidOperationException` if `Configure` never
  calls `UseHttp(...)`. No bug found in either package.
- **`Benzene.GoogleCloud.Functions.PubSub`** - `PubSubMiddlewareApplication.HandleAsync`'s null-outcome
  escalation (`context.MessageResult?.IsSuccessful != true`) already matches the corrected,
  safe-by-default convention (this was `#275`, already fixed - re-verified still correct on read, not
  re-reported). `AddGooglePubSub`'s DI registration set looked incomplete at first glance (no
  `AddMediaFormatNegotiation`/`IRequestMapper<PubSubContext>` registration, unlike `Benzene.Aws.Sqs`'s
  `AddSqs`) - traced this all the way through `Benzene.Core.MessageHandlers.DI.Extensions.AddContextItems`
  and confirmed it's a **false alarm**: `UseMessageHandlers<TContext>()` itself registers
  `IRequestMapper<>`/`IMediaFormatNegotiator<>`/`JsonMediaFormat<>` as **open generics** applicable to
  any `TContext` (via `AddMessageHandlers` → `AddContextItems`), so any pipeline that calls
  `.UseMessageHandlers()` - which `UsePubSub(action, ...)`'s `action` callback is expected to, exactly
  like every other transport - gets these registrations regardless of what the transport-specific
  `AddXxx()` does or doesn't register directly. `Benzene.Aws.Sqs`'s explicit registration of the same
  services is redundant-but-harmless (`TryAdd`), not evidence of a requirement `AddGooglePubSub` is
  missing.
- **`Benzene.Mesh.GoogleCloud.Storage`** (`GcsMeshArtifactStore`) - structurally identical to
  `S3MeshArtifactStore`/`BlobMeshArtifactStore` (`Key()` path-joining, `NotFound` → `null` on
  `TryReadAsync`, no `CancellationToken` parameter because `IMeshArtifactStore` itself declares none -
  consistent across all three cloud backends, not a GCS-specific gap). The `#242` path-traversal fix
  (`FileSystemMeshArtifactStore.ResolveWithinRoot`) doesn't apply here: GCS/S3 object keys aren't a
  real filesystem path, so a `..`-containing relative path is just literal key characters, not an
  escape - correctly out of scope for a blob-storage backend.
- **`RabbitMqMandatoryPublishCoordinator`'s own channel-recovery handling** - by contrast with finding
  #1, this file gets it right: `OnChannelShutdownAsync` (`RabbitMqMandatoryPublishCoordinator.cs:307-321`)
  faults every outstanding pending publish rather than leaving a caller awaiting a `Basic.Ack` that
  will never come once the channel's sequence-number/tag state has reset, and the coordinator itself
  is re-fetched fresh (via `GetOrCreate`'s `ConditionalWeakTable` lookup, re-validating publisher
  confirms) the next time a mandatory publish runs on the (recovered) channel. This is the fix pattern
  finding #1 recommends porting to the consuming side.
- **`RabbitMqBenzeneMessageClient`/`RabbitMqContextConverter`/`OutboundRabbitMqContextConverter`** -
  reused the shared static `ISerializer`, correctly fall back to `NullLogger` when `logger` is null
  (#266), and correctly forward the Benzene header dictionary onto `BasicProperties.Headers`. No
  finding.
- **`Benzene.RabbitMq`'s getters** (`RabbitMqMessageTopicGetter`, `RabbitMqMessageHeadersGetter`,
  `RabbitMqMessageBodyGetter`) - correct `byte[]`/string dual handling for AMQP's untyped header
  values, correct routing-key fallback, correct topic-header-not-set → `null` (not throwing) handling.
  No finding.

---

## Overall assessment

This territory earned its "first-ever pass" framing in exactly one place: the RabbitMQ worker's
connection/channel-recovery story, which - despite the package's `CLAUDE.md` confidently stating
"automatic recovery... transparently re-established" - has never actually been traced against the
specific AMQP delivery-tag-scoping boundary that makes that claim incomplete for a handler in flight
across the drop. That the *outbound* mandatory-publish path already got exactly this treatment
(`OnChannelShutdownAsync`, task board #30/#45) while the *inbound* consuming path never did is a
telling asymmetry - not a hypothetical, since the fix pattern needed already exists, tested, elsewhere
in the same package. The second finding (Pub/Sub's outbound client missing the ambient-cancellation
threading its two siblings already received) is a smaller, single-file, mechanical gap of the same
"the fix pattern already exists in this codebase, just wasn't carried everywhere" shape - worth noting
that both real findings in this round follow that pattern rather than being novel defect classes.

Everything else in scope - `Benzene.Kafka.Core`'s rebalance/offset-commit machinery,
`Benzene.Aws.Lambda.Kafka`'s per-partition ordering, `Benzene.Azure.Function.Kafka`'s settlement
carve-outs, the three GCP Cloud Functions Gen2 trigger-shape packages, and the GCS mesh artifact store
- held up against a genuinely adversarial read. Rounds 10 and 17 already put this codebase's highest-
value Kafka rebalance/settlement findings to bed (the partitions-lost handler, the
`RaiseOnFailureStatus` null-vs-failure convention, the dead-letter offset-withholding), and re-tracing
those paths from scratch here confirmed rather than extended them.
