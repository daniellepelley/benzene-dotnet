# Benzene.RabbitMq

## What this package does
RabbitMQ transport for Benzene: a self-hosted consumer worker (`RabbitMqWorker`) with per-message
ack/nack, plus an outbound publish client (`RabbitMqBenzeneMessageClient`). Built on the
`RabbitMQ.Client` v7 async API (Apache-2.0/MPL-2.0 - no licensing trap). This is the first
vendor-neutral, self-hosted broker in Benzene (every other broker is a cloud vendor's, plus Kafka);
it fills the on-prem/Kubernetes/multi-cloud gap. One of the "self-hosted worker" startup modes in
`docs/hosting.md` - Benzene owns the process, like `Benzene.Kafka.Core`/`Benzene.Azure.ServiceBus`,
unlike the Lambda/Functions triggers.

## ⚠️ Ack policy: safe by default, unlike the Kafka/ServiceBus triggers
`RabbitMqConfig.AckMode` defaults to `RabbitMqAckMode.Explicit`: a delivery is `BasicAck`ed on
handler success and `BasicNack`ed on a failure `IBenzeneResult`, a null/unestablished outcome
(`MessageResult` never set - typically an unrouted delivery whose topic matched no handler; see
"Null-outcome policy" below), **or** a thrown exception. RabbitMQ's first-class per-message ack (the
Service Bus `Explicit` model, not Kafka's offset watermark) makes this the natural default and a real
advantage - a failed message is redelivered or dead-lettered rather than silently lost.
`RabbitMqAckMode.AutoAck` (broker acks on dispatch, before the handler runs) is available for
at-most-once, loss-tolerant workloads. Because redelivery can reprocess a message, handlers must be
idempotent - see [Idempotency](../../docs/cookbooks/idempotency.md).

## Null-outcome policy: nack, not ack (reversed 2026-08-25)
A delivery whose handler pipeline never records a `MessageResult` - overwhelmingly an unrouted
message, no handler matched the topic - is nacked (`BasicNackAsync`) exactly like an explicit failure
result, **not** accepted as success. This is a deliberate reversal of this package's previous
documented-and-tested behaviour (`RabbitMqWorkerTest.NoResultRecorded_Acks`, now
`NoResultRecorded_Nacks`): RabbitMQ has a DLX and a bounded single requeue (see "Ack/nack policy"
below), so an unestablished outcome has somewhere safe to land instead of vanishing silently. See
`work/settlement-consistency-fix-plan.md` (row 7 and its decision-register entry) for the full
reasoning and why this overturns a written decision rather than filling a gap.

## Key types/interfaces

### Inbound (`RabbitMqMessage/`)
- `RabbitMqWorker : IBenzeneWorker` - opens an `IConnection`/`IChannel` (v7 async API) via
  `IRabbitMqConnectionFactory`, sets prefetch QoS, and consumes with an `AsyncEventingBasicConsumer`.
  Deliveries are **pushed** (not hand-polled like Kafka) and fanned out through
  `Benzene.SelfHost.BoundedConcurrentDispatcher<T>` so up to `ConcurrentRequests` handlers run at
  once; prefetch bounds unacked deliveries. `StartAsync` connects + starts consuming and returns;
  `StopAsync` cancels the consumer (`BasicCancelAsync`), drains in-flight handlers (up to
  `DrainTimeout`), then closes the channel and connection. The delivery body is a rented buffer only
  valid for the consumer callback, so the worker **copies it** (`Body.ToArray()`) before handing off
  to a dispatcher lane - do not remove that copy.
- `RabbitMqApplication : MiddlewareApplication<BasicDeliverEventArgs, RabbitMqContext, IBenzeneResult?>` -
  maps each delivery to a `RabbitMqContext`, runs it through the pipeline (tagged transport
  `"rabbitmq"`) in one DI scope per message, and returns the recorded `IBenzeneResult` the worker
  reads for ack. Mirrors `ServiceBusConsumerApplication`.
- `RabbitMqContext : IHasMessageResult` - wraps `BasicDeliverEventArgs` (context purity: transport
  shape only), carries `MessageResult`.
- Getters: `RabbitMqMessageTopicGetter` (topic from the topic header, **falling back to the AMQP
  routing key**; wrapped in `PresetTopicMessageTopicGetter` so `.UsePresetTopic(...)` works),
  `RabbitMqMessageBodyGetter` (UTF-8 body), `RabbitMqMessageHeadersGetter` (decodes the
  `byte[]`-valued `BasicProperties.Headers`, so W3C-trace/correlation/version header decorators
  work), `RabbitMqMessageHandlerResultSetter` (`MessageHandlerResultSetterBase`).
- **Ack/nack policy** (`RabbitMqWorker`, `Explicit` mode): success → `BasicAck`; failure result or
  exception → `BasicNack`. Requeue is governed by `RequeueOnFailure` and **bounded to one retry**: a
  first-attempt failure requeues, an already-`Redelivered` failure is nacked without requeue (to the
  DLX / dropped) so a poison message can't hot-loop. `RequeueOnFailure = false` always nacks without
  requeue (straight to DLX). RabbitMQ's `Redelivered` is a boolean, not a count - for a higher,
  precise redelivery limit, use a dead-letter exchange + queue policy on the broker (quorum-queue
  delivery-count features are out of scope).
- **`AddRabbitMq` must register everything `.UseMessageHandlers()` resolves** per `RabbitMqContext`:
  the four getters, `IMessageVersionGetter` (`HeaderMessageVersionGetter`),
  `AddMediaFormatNegotiation`, and `IRequestMapper` (`MultiSerializerOptionsRequestMapper`). A gap
  wouldn't throw visibly - the worker nacks handler faults - so it surfaces only as messages never
  handled; `RabbitMqRealPipelineTest` drives the real DI + routing (no broker) to catch it,
  mirroring `ServiceBusConsumerRealPipelineTest`.
- `IRabbitMqConnectionFactory` / `RabbitMqConnectionFactory` - the connection seam (mirrors
  `IKafkaConsumerFactory`/`IServiceBusClientFactory`): the caller builds the `ConnectionFactory`
  (host, credentials, vhost, TLS, automatic recovery - on by the SDK's default), the worker owns the
  channel and disposes both on stop.
- `Extensions.UseRabbitMq(IBenzeneWorkerStartup, config, connectionFactory, action)` - the worker
  wiring, mirroring `UseKafka`/`UseServiceBus`; registers `AddBenzeneMessage().AddRabbitMq()`.

### Health check
- `RabbitMqHealthCheck` - verifies the broker is reachable and the consumed queue exists, via a
  **passive** `QueueDeclarePassiveAsync` (read-only: it neither creates nor mutates the queue; a missing
  queue is a channel-level `404`). `Type = "RabbitMq"`, dependency `("Queue", config.QueueName)`.
  Non-destructive (no publish/get/ack). AMQP reply codes align with §3.9 (reversed): `403 access-refused`
  (permission) -> a **persistent `Failed`**, surfacing as unhealthy rather than being softened to a
  Warning (a deterministic misconfiguration that won't self-heal); `404 not-found` -> a transient Failed;
  classified via `HealthCheckError.Classify`, never the message.
- `IRabbitMqConnectionProvider` / `RabbitMqConnectionProvider` - supplies the connection the check uses.
  It opens **one** connection (via `IRabbitMqConnectionFactory`) and reuses it across probes, opening
  only a cheap short-lived **channel** per probe. It does **not** share the worker's private connection
  (a deliberate simplification, consistent with the Kafka admin client's dedicated-reused-handle) - so
  there is one extra idle connection, not a per-probe connect. Unlike a `Lazy<Task>`, a failed connect is
  not memoised (the next probe retries) and a dropped connection is re-opened.
- **Auto-wired (Phase 4, default-on):** `UseRabbitMq(..., healthCheck: true)` calls
  `AddRabbitMqDependencyHealthCheck(config, connectionFactory)` - registers on the **dependency**
  category (deep `healthcheck` layer only, never a probe - a broker blip is shared-fate; see
  `IDependencyHealthCheck`), dedup `"RabbitMq:{queue}"`. `healthCheck: false` opts out; explicit
  `AddRabbitMqHealthCheck(config, factory)` on an `IHealthCheckBuilder` is the manual path.

### Outbound (`RabbitMqSendMessage/`)
- `RabbitMqBenzeneMessageClient : IBenzeneMessageClient` - publishes so business logic depends only
  on `IBenzeneMessageSender`/`IBenzeneMessageClient`. Mirrors `KafkaBenzeneMessageClient`, including
  the shared static `ISerializer` (a fresh `JsonSerializer` per send would defeat System.Text.Json's
  per-options converter cache) and the second (prebuilt-pipeline) constructor for testing. Both
  constructors fall back to `NullLogger<RabbitMqBenzeneMessageClient>.Instance` when `logger` is null
  (#266, WP-I, the P8 sweep landing the `Benzene.Clients.*` outbound-client family's #192 null-logger
  fix on this sibling), so a null-logger construction can't make the `catch` block's own `LogError`
  call throw and mask the real publish failure.
- `RabbitMqContextConverter<T>` - the request `Topic` becomes the AMQP **routing key** and is also
  carried as a `"topic"` **header**, so a Benzene consumer routes by header (portable, matching every
  other transport) with the routing key as the idiomatic fallback. Forwards the Benzene header
  dictionary onto `BasicProperties.Headers` (UTF-8 encoded).
- `RabbitMqClientMiddleware` / `.UseRabbitMqClient(channel)` - the publish middleware
  (`BasicPublishAsync`); `.UseRabbitMq<T>(exchange, ...)` is the `OutboundRoutingBuilder` conversion
  entry point, mirroring Kafka's `.UseKafka<T>(...)`.
- Publish is **persistent by default** (delivery mode 2) so a message on a durable queue survives a
  broker restart; pass `.UseRabbitMqClient(channel, persistent: false)` for transient delivery. This
  is a behavioral change from earlier versions, which always published transient.
- Publish is fire-and-forget by default (maps a completed publish to `Accepted`, a thrown publish to
  `ServiceUnavailable`). `BasicPublishAsync` returns as soon as the frame is written to the socket -
  **`CreateChannelOptions.PublisherConfirmationsEnabled = true` alone does not make it await the
  broker's ack** (verified against the RabbitMQ.Client 7.0.0 source: that only happens when
  `PublisherConfirmationTrackingEnabled` is *also* set, which is a separate, unrelated feature this
  package does not use - see the `mandatory: true` bullet below for how outcomes are actually awaited).
- `mandatory: true` (`RabbitMqClientMiddleware`/`RabbitMqBenzeneMessageClient`/`.UseRabbitMqClient`) is
  a real, awaited guarantee (WP-8, task board #24 - it used to be documented but not implemented: the
  middleware set `Published = true` unconditionally and never subscribed to `BasicReturnAsync`).
  `RabbitMqMandatoryPublishCoordinator` - one instance per `IChannel`, looked up via a
  `ConditionalWeakTable` so the channel-scoped `BasicReturnAsync`/`BasicAcksAsync`/`BasicNacksAsync`
  events are subscribed exactly once no matter how many mandatory sends (and `RabbitMqClientMiddleware`
  instances - one is constructed fresh per publish) run against that channel - stamps a `MessageId` if
  the caller didn't set one (AMQP's `Basic.Return` carries the message's properties back but no
  delivery tag, so `MessageId` is the only correlation key available), publishes with a per-channel
  gate held just long enough to pair `GetNextPublishSequenceNumberAsync()` with the actual
  `BasicPublishAsync` call atomically (RabbitMQ.Client does not hand a publish its own assigned
  delivery tag back, so this pairing is the only race-free way to know it), and resolves the outcome
  from whichever of a `Basic.Return` (unroutable → failed) or `Basic.Ack`/`Basic.Nack` (routed/rejected)
  fires first for that tag. **Requires `channel` to have publisher confirmations enabled** - wiring
  throws immediately (not on first publish) if `GetNextPublishSequenceNumberAsync()` shows it doesn't
  (the only public-API-observable proxy for that setting in RabbitMQ.Client 7.0.0). A channel that
  closes with mandatory publishes still outstanding faults their callers rather than hanging them.
  Known boundary: correlation is only as good as the "nothing else races the gate" assumption holds -
  a channel shared with publishing that bypasses this middleware entirely (not `RabbitMqClientMiddleware`
  publishing without `mandatory`, which shares the same coordinator once one exists for the channel) can
  in principle interleave inside that narrow gate window. Dedicate the channel to Benzene's outbound
  middleware for `mandatory: true` traffic to avoid it.
- **Hardening (tracked findings round 7-10, task board #30/#33/#45 - WP-A):** ruled in
  [`work/archive/bug-fix-designs-round7-10-2026-08.md`](../../work/archive/bug-fix-designs-round7-10-2026-08.md)
  §"WP-A - RabbitMQ mandatory-publish coordinator hardening".
  - **Fenced cleanup on cancellation/timeout (#30/#45).** `PublishMandatoryAsync`'s final
    `tcs.Task.WaitAsync(...)` is awaited on a token linked from the caller's `cancellationToken` AND a
    `publishConfirmTimeout` (default `RabbitMqMandatoryPublishCoordinator.DefaultPublishConfirmTimeout`,
    30s) - mirroring `Benzene.Resilience.TimeoutMiddleware`'s "did the timer fire, or did the host token
    fire" distinction. **Either way**, the pending-publish entry is `Forget`-ed from `_byTag`/
    `_byMessageId` **before** the exception propagates - previously only the publish-time try/catch
    called `Forget`, so a caller cancelling (or, before #45, simply waiting forever) while genuinely
    awaiting the broker's ack/nack/return leaked the entry permanently. A timer-caused wait failure
    surfaces as `TimeoutException` (distinguishable from a genuine caller cancellation, which still
    surfaces as `OperationCanceledException`). Threaded end-to-end: `publishConfirmTimeout` is an
    optional parameter on `RabbitMqMandatoryPublishCoordinator.PublishMandatoryAsync`,
    `RabbitMqClientMiddleware`'s constructor, `Extensions.UseRabbitMqClient`, and
    `RabbitMqBenzeneMessageClient`'s constructor - `null` at any layer means "use the coordinator's
    default".
  - **Reject a duplicate in-flight `MessageId` (#33).** `_byMessageId[messageId] = pending` used
    indexer-overwrite, so publishing a second mandatory message with a `MessageId` still pending from an
    earlier publish silently stole that earlier publish's correlation entry - a later `Basic.Return`
    naming the shared `MessageId` would then resolve the wrong publish's `Tcs` (and the true owner's
    `Tcs` would never settle from a real broker event again). Now `TryAdd`; a duplicate throws
    `InvalidOperationException` at publish time, before the message reaches the wire. Currently
    unreachable through the shipped `RabbitMqClientMiddleware` surface (it always stamps a fresh GUID
    when the caller didn't set one), but the coordinator's own public contract - and a caller who
    supplies their own `MessageId` - invites it.

## Configurable topic header key
The topic header key defaults to `RabbitMqConstants.DefaultTopicHeader` (`"topic"`) but is **not
hard-coded** - override it on each side to interoperate with a non-Benzene producer/consumer that
carries the topic on a different header, without writing a custom `IMessageTopicGetter`/converter:
- **Consumer**: `RabbitMqConfig.TopicHeaderKey` (threaded by `UseRabbitMq` into
  `AddRabbitMq(topicHeaderKey)`, which constructs `new RabbitMqMessageTopicGetter(topicHeaderKey)`).
  The bare `AddRabbitMq()` / `new RabbitMqMessageTopicGetter()` keep the default.
- **Producer**: the `topicHeaderKey` argument on the outbound `.UseRabbitMq<T>(...)` extensions,
  `RabbitMqBenzeneMessageClient`, and `RabbitMqContextConverter<T>` - all default to the same
  constant.
Keep the producer's and consumer's keys in sync. The routing-key fallback is unaffected: a message
lacking the configured header still routes by its AMQP routing key.

## When to use this package
- Consuming/producing RabbitMQ from a long-running process (console, container, Kubernetes) via
  `Benzene.HostedService` / `Benzene.SelfHost`.

## Deliberate boundaries (NOT shipped)
- RPC-over-RabbitMQ (`reply-to`/direct-reply request/response); RabbitMQ **Streams** (a distinct
  offset model, closer to Kafka); topology *management* (declaring exchanges/queues/bindings, or
  generating them from `[Message]` topics) - the worker assumes the queue and any DLX exist, as the
  Kafka worker assumes topics do; quorum-queue-specific features. **NATS.Net** is the next
  self-hosted broker candidate and is deferred. See `work/archive/rabbitmq-plan-2026-08.md`.

## Dependencies on other Benzene packages
- **Benzene.Clients** - `IBenzeneMessageClient`, outbound seam.
- **Benzene.Core.MessageHandlers** - routing, mappers, `TransportMiddlewarePipeline`,
  `MessageHandlerResultSetterBase`, `PresetTopicMessageTopicGetter`.
- **Benzene.SelfHost** - `IBenzeneWorkerStartup`, `BoundedConcurrentDispatcher<T>` (and
  `IBenzeneWorker` via `Benzene.Abstractions.Pipelines`).
- **RabbitMQ.Client** (v7) - the broker client.

## Test coverage
- Unit (`test/Benzene.Core.Test/RabbitMq/`, no broker): `RabbitMqGettersTest` (topic header vs
  routing-key fallback, body, header decoding), `RabbitMqApplicationTest` (delivery→context→result),
  `RabbitMqWorkerTest` (drives real deliveries through the `AsyncEventingBasicConsumer` against a
  mocked `IChannel`: success→ack, failure→nack-requeue, redelivered-failure→nack-no-requeue,
  requeue-disabled, exception→nack, no-result→nack, AutoAck mode, config defaults),
  `RabbitMqBenzeneMessageClientTest` (status mapping + topic-as-routing-key + header forwarding),
  `RabbitMqRealPipelineTest` (real DI registration completeness). `RabbitMqMandatoryPublishTest` also
  covers the WP-A hardening directly against `RabbitMqMandatoryPublishCoordinator`: cancelling mid-wait
  and the publish-confirm timeout both forget the pending-publish entry (read back via reflection on
  the private `_byTag`/`_byMessageId` dictionaries - the round-7 leak-probe technique), and a duplicate
  in-flight `MessageId` throws `InvalidOperationException` without disturbing the earlier publish.
- Live (`test/Benzene.Integration.Test/RabbitMq/`, CI-only, needs Docker): `RabbitMqWorkerLiveTest`
  round-trips a real message through a real broker, mirroring `BenzeneKafkaWorkerLiveTest`.

## Claim-check hydration
Not wired here yet: `Benzene.ClaimCheck`'s hydrate middleware needs an
`IMessageBodySetter<RabbitMqContext>` registered — the same 5-line pattern as
`Benzene.Aws.Lambda.Sqs`/`.Sns`/`.EventBridge` ship (see `work/archive/claim-check-plan-2026-08.md` Phase 2 step 4).
