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
/// Declares that a service sends <see cref="Topic"/> (payload <see cref="MessageType"/>) downstream over
/// <see cref="Transport"/>, routed at runtime to the target whose identifier is in the
/// <see cref="TargetEnvVar"/> environment variable — the ingress the target service already consumes.
/// Used by <see cref="MeshServiceWiring.ConfigureServices"/> to both surface the topic in the spec's
/// <c>events</c> (→ structural topology) and register the outbound route on the chosen transport.
/// </summary>
/// <param name="Topic">The topic sent downstream (e.g. <c>payments:capture</c>).</param>
/// <param name="MessageType">The payload type (for the spec schema).</param>
/// <param name="Transport">Which AWS transport carries it (SQS / SNS / EventBridge).</param>
/// <param name="TargetEnvVar">
/// The env var holding the transport's target identifier — an SQS queue URL, an SNS topic ARN, or an
/// EventBridge event bus name (e.g. <c>PAYMENTS_QUEUE_URL</c>, <c>ORDER_PLACED_TOPIC_ARN</c>, <c>EVENT_BUS_NAME</c>).
/// </param>
/// <param name="DeclareAsEvent">
/// Whether the send is a domain edge that belongs in the spec's <c>events</c> and so on the mesh
/// topology (the default), or pure plumbing that should be routed but not drawn. The one case for
/// <c>false</c> so far is <c>benzene:healthcheck</c>: a generated client always lists it in its
/// <c>RequiredTopics</c>, so a consumer must register a route or start-up fails — but "orders probes
/// payments' health" is not a business edge and would be noise on the topology graph.
/// </param>
public record OutboundSend(
    string Topic,
    Type MessageType,
    OutboundTransport Transport,
    string TargetEnvVar,
    bool DeclareAsEvent = true)
{
    /// <summary>Convenience for the common SQS command hop.</summary>
    public static OutboundSend Sqs(string topic, Type messageType, string queueUrlEnvVar)
        => new(topic, messageType, OutboundTransport.Sqs, queueUrlEnvVar);

    /// <summary>
    /// Routes a generated client's mandatory <c>benzene:healthcheck</c> topic over the same transport as
    /// the service it probes, without drawing it as a topology edge. Needed because every generated
    /// client requires that topic unconditionally — see the AwsMesh README's codegen notes.
    /// </summary>
    public static OutboundSend HealthCheck(OutboundTransport transport, string targetEnvVar)
        => new(Benzene.Abstractions.BenzeneTopic.HealthCheck, typeof(Benzene.Abstractions.Results.Void),
            transport, targetEnvVar, DeclareAsEvent: false);

    /// <summary>Convenience for an SNS fan-out event.</summary>
    public static OutboundSend Sns(string topic, Type messageType, string topicArnEnvVar)
        => new(topic, messageType, OutboundTransport.Sns, topicArnEnvVar);

    /// <summary>Convenience for an EventBridge routed integration event.</summary>
    public static OutboundSend EventBridge(string topic, Type messageType, string eventBusNameEnvVar)
        => new(topic, messageType, OutboundTransport.EventBridge, eventBusNameEnvVar);
}
