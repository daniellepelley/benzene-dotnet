namespace Benzene.Abstractions;

/// <summary>
/// The reserved names this service reads and writes on the wire (wire-contracts.md §2).
/// </summary>
/// <remarks>
/// <para>
/// This type exists as the single definition of the DEFAULT names — each binding's own
/// <c>DefaultTopicAttribute</c>/<c>DefaultTopicProperty</c> constant aliases it rather than
/// repeating the literal.
/// </para>
/// <para>
/// <strong>It IS a DI seam for the standalone (non-Lambda, non-Azure-Function) consumer
/// packages</strong> — <c>AddSqsConsumer</c>, <c>AddServiceBusConsumer</c>, <c>AddEventHubConsumer</c>,
/// and <c>AddRabbitMq</c>: each resolves a registered <see cref="IBenzeneWireNames"/> (falling back to
/// <see cref="Default"/>) to seed its topic getter's key, but only when the caller left that
/// transport's own topic-attribute/-property/-header parameter at ITS default — an explicit
/// non-default value passed to the transport's Add/Use extension, or set directly on its config
/// (<c>SqsConsumerConfig.TopicAttributeKey</c>, <c>BenzeneServiceBusConfig.TopicPropertyKey</c>,
/// <c>BenzeneEventHubConfig.TopicPropertyKey</c>, <c>RabbitMqConfig.TopicHeaderKey</c>), always wins
/// over a registered <see cref="IBenzeneWireNames"/>. Kafka has no equivalent knob — for Kafka the
/// topic is the Kafka topic itself (a routing concept), not a message attribute.
/// </para>
/// <para>
/// <strong>It is NOT (yet) resolved by outbound clients</strong>, the Lambda-triggered consumer
/// packages (<c>Benzene.Aws.Lambda.Sqs</c>/<c>.Sns</c>/<c>.Kafka</c>), the Azure Functions-triggered
/// consumer packages, or GoogleCloud Pub/Sub — those still only take the topic-attribute/-property
/// parameter directly. <strong>An override applies to both directions:</strong> the same name must be
/// used by the service's inbound bindings and its outbound clients, so today a service must still
/// configure its outbound clients to match whatever <see cref="IBenzeneWireNames"/> (or the
/// transport-specific parameter) it set on the inbound side. Overriding only one side would send
/// messages the service cannot itself receive, and the symptom — a message that arrives and never
/// routes — is indistinguishable from a missing handler.
/// </para>
/// <para>
/// The defaults are what let two untouched Benzene services interoperate; a service that changes
/// one is opting out of that and is responsible for agreeing the change with whatever it talks to.
/// </para>
/// </remarks>
public interface IBenzeneWireNames
{
    /// <summary>
    /// The metadata key carrying the topic on transports that do not use the envelope (SQS/SNS
    /// message attributes, Service Bus/Event Hub properties, Kafka/RabbitMQ headers, Pub/Sub
    /// attributes). Defaults to <c>topic</c> — the same spelling as the envelope's own field, so
    /// one concept has one name wherever it travels.
    /// </summary>
    string Topic { get; }

    /// <summary>
    /// The metadata key carrying the payload's schema version. Defaults to <c>benzene-version</c>.
    /// </summary>
    string Version { get; }
}

/// <summary>
/// The default <see cref="IBenzeneWireNames"/>, and the single definition of each default name.
/// </summary>
/// <remarks>
/// Every binding's own <c>DefaultTopicAttribute</c>/<c>DefaultTopicProperty</c> constant aliases
/// <see cref="DefaultTopic"/> rather than repeating the literal. That matters: when each binding
/// spelled the name itself, they were free to drift apart, and a binding that did could pass every
/// other test while being unable to exchange a message with its own siblings.
/// </remarks>
public class BenzeneWireNames : IBenzeneWireNames
{
    /// <summary>
    /// The default topic metadata key: <c>topic</c>.
    /// </summary>
    public const string DefaultTopic = "topic";

    /// <summary>
    /// The default payload-version metadata key: <c>benzene-version</c>.
    /// </summary>
    public const string DefaultVersion = "benzene-version";

    /// <summary>
    /// The spec's defaults. Used wherever a service has not registered its own.
    /// </summary>
    public static readonly IBenzeneWireNames Default = new BenzeneWireNames();

    /// <summary>
    /// Initializes names, defaulting any not given to the spec's default.
    /// </summary>
    /// <param name="topic">The topic metadata key. Defaults to <see cref="DefaultTopic"/>.</param>
    /// <param name="version">The version metadata key. Defaults to <see cref="DefaultVersion"/>.</param>
    public BenzeneWireNames(string topic = DefaultTopic, string version = DefaultVersion)
    {
        Topic = topic;
        Version = version;
    }

    /// <inheritdoc />
    public string Topic { get; }

    /// <inheritdoc />
    public string Version { get; }
}
