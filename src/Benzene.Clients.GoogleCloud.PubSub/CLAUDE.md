# Benzene.Clients.GoogleCloud.PubSub

## What this package does
Outbound Google Cloud Pub/Sub client for a Benzene app: publish a message to a Pub/Sub topic. The
egress counterpart of `Benzene.GoogleCloud.Functions.PubSub` (the ingress push adapter) — ships the
client half of Phase 2 in `work/archive/google-cloud-roadmap-1.0.md` (see that document's
2026-08-22 correction note). Pins **only** `Google.Cloud.PubSub.V1`.

## Key types
- `PubSubClientMiddleware` — terminal `IMiddleware<PubSubSendMessageContext>`; publishes via
  `PublisherServiceApiClient.PublishAsync(TopicName, IEnumerable<PubsubMessage>)` and records the
  server-assigned message id on the context. A publish failure throws — Pub/Sub has no per-message
  HTTP status the way SQS/SNS do, so there's no failure branch to map, unlike
  `Benzene.Clients.Aws.Sqs`/`Benzene.Clients.Aws.Sns`'s `NotPersisted`-style handling.
- `PubSubSendMessageContext` — holds the resolved `TopicName`, the built `PubsubMessage`, and the
  `MessageId` `PubSubClientMiddleware` fills in after a successful publish.
- `OutboundPubSubContextConverter` — the `Benzene.Clients.OutboundContext` counterpart, used by the
  `OutboundContext` overloads of `.UsePubSub(topic, ...)` for
  `AddOutboundRouting(...).Route(topic, ...)`. Always maps to `IBenzeneResult<Void>` — Pub/Sub is
  fire-and-acknowledge, so a topic routed here must be sent via
  `IBenzeneMessageSender.SendAsync<TRequest, Void>`.
- `Extensions` — `UsePubSubClient` (both a given-`PublisherServiceApiClient` overload and a
  DI-resolved overload) and `UsePubSub` (the `OutboundContext` overloads).

## Conventions
- `OutboundPubSubContextConverter` forwards `OutboundContext.Headers` onto `PubsubMessage.Attributes`
  and separately writes the Benzene routing topic to a `topic` attribute (`DefaultTopicAttribute`,
  configurable via `topicAttributeKey` — keep it in sync with the consumer's key). This is the
  *outbound* counterpart of the inbound `PubSubMessageTopicGetter` in
  `Benzene.GoogleCloud.Functions.PubSub`, which reads the same attribute — the same
  "topic in a custom attribute" convention SQS/SNS/Service Bus already established. As with SNS, an
  empty header value and an empty/null `contextIn.Topic` are both skipped rather than written as an
  empty attribute (Pub/Sub's `Attributes` map doesn't reject an empty value the way SNS's API call
  does, but there's no reason to publish one either).
- **Topic resolution accepts either shape.** `OutboundPubSubContextConverter`'s constructor takes a
  `topic` string and resolves it via `TopicName.Parse` if it starts with `"projects/"` (a full
  resource path — what Terraform outputs / env vars typically carry), or via
  `TopicName.FromProjectTopic` against the `GOOGLE_CLOUD_PROJECT` environment variable otherwise (a
  bare topic id, convenient for local/dev — falls back to `"local-project"` if the env var is unset).
- Two rungs of shorthand, same shape as every sibling client package: `UsePubSub(topic,
  topicAttributeKey:)` resolves `PublisherServiceApiClient` from DI via `UsePubSubClient()`;
  `UsePubSub(topic, action, topicAttributeKey:)` hands you the inner
  `IMiddlewarePipelineBuilder<PubSubSendMessageContext>` to configure yourself (e.g.
  `action.UsePubSubClient(publisherClient)` for an explicit client, or extra middleware around the
  publish). There is no `UsePubSub(app, PublisherServiceApiClient, ...)` one-argument shorthand the
  way `Benzene.RabbitMq`'s `UseRabbitMq(app, IChannel, ...)` has — go through the `action` overload
  for an explicit client.
- `PublisherServiceApiClient` is Google's own abstract, non-sealed GAX client type — mockable
  directly with Moq (no wrapper interface needed the way some SDKs require).

## Not yet built — deliberately out of scope for this package
No batch client (`IBenzeneBatchMessageClient`, mirroring `SnsBatchMessageClient`/
`EventGridBatchMessageClient`), no request/response client, and no auto-wired health check
(mirroring `SqsHealthCheck`/`SnsHealthCheck`/`EventHubHealthCheck`) exist yet. Pub/Sub does have a
cheap non-destructive reachability call (`PublisherServiceApiClient.GetTopic`), unlike Event Grid, so
a health check here is a plausible small follow-up rather than a structurally-blocked one — just not
built in this pass. See `work/archive/google-cloud-roadmap-1.0.md`'s 2026-08-22 correction note for
the full list of what Phase 2 still leaves open (the pull-subscription background worker is the
larger remaining piece, not scoped to this package at all).

## Dependencies
`Google.Cloud.PubSub.V1`; Benzene `Core.Middleware`, `Clients`, `Results`.
