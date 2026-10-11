namespace Benzene.Examples.AwsMesh.Shared;

/// <summary>
/// The AWS messaging transport an <see cref="OutboundSend"/> is carried over. Each is used for what
/// it's idiomatically good at: <see cref="Sqs"/> for a point-to-point command to one consumer,
/// <see cref="Sns"/> for an event fanned out to many subscribers, <see cref="EventBridge"/> for a
/// routed integration event.
/// </summary>
public enum OutboundTransport
{
    /// <summary>Point-to-point command queue — one consumer. <c>TargetEnvVar</c> holds the queue URL.</summary>
    Sqs,

    /// <summary>Pub/sub event, one publisher → many subscribers. <c>TargetEnvVar</c> holds the topic ARN.</summary>
    Sns,

    /// <summary>Routed integration event. <c>TargetEnvVar</c> holds the event bus name (source = the sending service).</summary>
    EventBridge
}

/// <summary>
/// Who owns an outbound topic's contract (contract-document.md §3) - not the transport, and not whether
/// a reply comes back.
/// </summary>
public enum OutboundKind
{
    /// <summary>
    /// A topic this service publishes and <b>defines</b> (e.g. orders' <c>order:placed</c>): the contract
    /// document's <c>events[]</c>, part of what this service's clients depend on.
    /// </summary>
    Event,

    /// <summary>
    /// A message to a topic <b>another party</b> defines (e.g. orders' <c>payments:capture</c>, owned by
    /// payments): the contract document's <c>sends[]</c>, a dependency of this service.
    /// </summary>
    Send
}

/// <summary>
/// Declares that a service puts <see cref="Topic"/> (payload <see cref="MessageType"/>) on the wire over
/// <see cref="Transport"/>, routed at runtime to the target whose identifier is in the
/// <see cref="TargetEnvVar"/> environment variable — the ingress the target service already consumes.
/// Used by <see cref="MeshServiceWiring.ConfigureServices"/> to both declare the topic - an
/// <see cref="OutboundKind.Event"/> in the contract document's <c>events</c>, a
/// <see cref="OutboundKind.Send"/> in its <c>sends</c> (both → the mesh's structural topology) - and
/// register the outbound route on the chosen transport.
/// </summary>
/// <param name="Topic">The topic sent downstream (e.g. <c>payments:capture</c>).</param>
/// <param name="Kind">Whether this service defines the topic (an event) or another party does (a send).</param>
/// <param name="MessageType">The payload type (for the spec schema).</param>
/// <param name="Transport">Which AWS transport carries it (SQS / SNS / EventBridge).</param>
/// <param name="TargetEnvVar">
/// The env var holding the transport's target identifier — an SQS queue URL, an SNS topic ARN, or an
/// EventBridge event bus name (e.g. <c>PAYMENTS_QUEUE_URL</c>, <c>ORDER_PLACED_TOPIC_ARN</c>, <c>EVENT_BUS_NAME</c>).
/// </param>
/// <param name="Outboxed">
/// When <see langword="true"/>, <see cref="MeshServiceWiring.ConfigureServices"/> adds
/// <c>Benzene.Outbox</c>'s <c>UseOutbox()</c> to this route's pipeline (after the trace/correlation
/// stamping middleware, before the terminal transport converter) — see
/// <c>work/archive/outbox-plan-2026-08.md</c> §2.1. Defaults to <see langword="false"/> so a service opts a route
/// into the outbox one send at a time, without dragging every other service's routes (or this
/// service's other routes) through it. The caller is also responsible for registering
/// <c>Benzene.Outbox</c>'s <c>AddOutbox(...)</c> and an <c>IOutboxStore</c> (e.g.
/// <c>AddDynamoDbOutboxStore</c>) — this flag only wires the per-route middleware.
/// </param>
/// <param name="ClaimChecked">
/// When <see langword="true"/>, <see cref="MeshServiceWiring.ConfigureServices"/> adds
/// <c>Benzene.ClaimCheck</c>'s <c>UseClaimCheck()</c> to this route's pipeline — placed AFTER
/// <c>UseOutbox()</c> when <see cref="Outboxed"/> is also set, and before the terminal transport
/// converter (see <c>work/archive/claim-check-plan-2026-08.md</c> Phase 6). Ordering matters here: capture (the first,
/// terminal pass through <c>UseOutbox()</c>) needs the REAL typed <c>Request</c> to serialize into its
/// envelope, so offload must not run before it — it would replace <c>Request</c> with a placeholder the
/// envelope would then durably store instead of the real payload. Placing <c>UseClaimCheck()</c> after
/// <c>UseOutbox()</c> means it only ever runs on the relay dispatcher's pass-through (when the envelope
/// is re-sent with its real deserialized payload as <c>Request</c>) or, on a non-outboxed route, on
/// every send — either way it sees the actual request that is about to hit the wire. Defaults to
/// <see langword="false"/> so a service opts a route into claim-check one send at a time. The caller is
/// also responsible for registering <c>Benzene.ClaimCheck</c>'s <c>IClaimCheckStore</c> (e.g.
/// <c>AddS3ClaimCheckStore</c>) — this flag only wires the per-route middleware.
/// </param>
public record OutboundSend(
    string Topic,
    OutboundKind Kind,
    Type MessageType,
    OutboundTransport Transport,
    string TargetEnvVar,
    bool Outboxed = false,
    bool ClaimChecked = false)
{
    /// <summary>Convenience for the common SQS command hop.</summary>
    public static OutboundSend Sqs(string topic, OutboundKind kind, Type messageType, string queueUrlEnvVar, bool outboxed = false, bool claimChecked = false)
        => new(topic, kind, messageType, OutboundTransport.Sqs, queueUrlEnvVar, outboxed, claimChecked);

    /// <summary>Convenience for an SNS fan-out event.</summary>
    public static OutboundSend Sns(string topic, OutboundKind kind, Type messageType, string topicArnEnvVar, bool outboxed = false, bool claimChecked = false)
        => new(topic, kind, messageType, OutboundTransport.Sns, topicArnEnvVar, outboxed, claimChecked);

    /// <summary>Convenience for an EventBridge routed integration event.</summary>
    public static OutboundSend EventBridge(string topic, OutboundKind kind, Type messageType, string eventBusNameEnvVar, bool outboxed = false, bool claimChecked = false)
        => new(topic, kind, messageType, OutboundTransport.EventBridge, eventBusNameEnvVar, outboxed, claimChecked);
}
