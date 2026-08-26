using Microsoft.CodeAnalysis;

namespace Benzene.Azure.Function.SourceGenerators
{
    /// <summary>
    /// Every diagnostic <see cref="AzureFunctionTriggerGenerator"/> (or a transport reader under
    /// <c>Transports/</c>) can report, in one place. Future generator complaints should add a
    /// descriptor here rather than inventing a new reporting mechanism - see
    /// <c>work/bug-fix-designs-2026-08.md</c>, WP-5a, and <c>work/archive/bug-fix-designs-round7-10-2026-08.md</c>,
    /// WP-C.
    /// </summary>
    internal static class DiagnosticDescriptors
    {
        private const string Category = "Benzene.SourceGenerators";

        /// <summary>
        /// Two (or more) generated triggers would emit the same <c>[Function(name)]</c> literal. This
        /// is checked globally across every transport - not per-transport - because Azure Functions
        /// doesn't know or care which binding produced the name: a
        /// <c>BenzeneQueueTrigger(Name = "dup")</c> and a <c>BenzeneKafkaTrigger(Name = "dup")</c> in
        /// the same compilation collide exactly as two queue triggers named "dup" would.
        ///
        /// Deliberately NOT auto-renamed the way the generated class name is: a Function name is
        /// externally meaningful (bindings, host.json, scale rules, the portal's identity for the
        /// function), so silently picking a different one would only move the failure from build time
        /// to deployment time. The name is the user's to fix.
        /// </summary>
        public static readonly DiagnosticDescriptor DuplicateFunctionName = new(
            id: "BENZ0001",
            title: "Duplicate Azure Function name",
            messageFormat:
                "Azure Function name {0} is used by more than one Benzene trigger declaration in this " +
                "compilation. Function names must be unique across the app (Azure Functions doesn't " +
                "distinguish which transport produced the name), so this trigger was not generated - " +
                "give it a distinct Name.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneCosmosDbTrigger(...)]</c> declaration is missing the required
        /// <c>DocumentType</c> (the change-feed binding is generic over it). Previously this was
        /// silently skipped - a declared trigger that is silently not generated is the worst outcome,
        /// so it's reported and fails the build instead.
        /// </summary>
        public static readonly DiagnosticDescriptor CosmosDbTriggerMissingDocumentType = new(
            id: "BENZ0002",
            title: "CosmosDb trigger missing DocumentType",
            messageFormat:
                "[assembly: BenzeneCosmosDbTrigger(Name = {0}, ...)] does not set DocumentType. The " +
                "Cosmos DB change-feed trigger is generic over the document type and can't be " +
                "generated without it - set DocumentType = typeof(YourDocument).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneServiceBusTrigger(...)]</c> declaration sets neither <c>QueueName</c>
        /// nor <c>TopicName</c>, so there is no destination to bind the trigger to (WP-C, #39).
        /// </summary>
        public static readonly DiagnosticDescriptor ServiceBusTriggerMissingDestination = new(
            id: "BENZ0003",
            title: "ServiceBus trigger missing QueueName/TopicName",
            messageFormat:
                "[assembly: BenzeneServiceBusTrigger(Name = {0}, ...)] sets neither QueueName nor " +
                "TopicName. A Service Bus trigger must bind to exactly one queue or topic - set one of " +
                "QueueName or TopicName (with SubscriptionName).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneEventHubTrigger(...)]</c> declaration is missing the required
        /// <c>EventHubName</c> (WP-C, #39).
        /// </summary>
        public static readonly DiagnosticDescriptor EventHubTriggerMissingEventHubName = new(
            id: "BENZ0004",
            title: "EventHub trigger missing EventHubName",
            messageFormat:
                "[assembly: BenzeneEventHubTrigger(Name = {0}, ...)] does not set EventHubName. The " +
                "Event Hub trigger can't be bound without it - set EventHubName.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneKafkaTrigger(...)]</c> declaration is missing the required
        /// <c>Topic</c> (WP-C, #39).
        /// </summary>
        public static readonly DiagnosticDescriptor KafkaTriggerMissingTopic = new(
            id: "BENZ0005",
            title: "Kafka trigger missing Topic",
            messageFormat:
                "[assembly: BenzeneKafkaTrigger(Name = {0}, ...)] does not set Topic. The Kafka " +
                "trigger can't be bound without it - set Topic.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneQueueTrigger(...)]</c> declaration is missing the required
        /// <c>QueueName</c> (WP-C, #39).
        /// </summary>
        public static readonly DiagnosticDescriptor QueueStorageTriggerMissingQueueName = new(
            id: "BENZ0006",
            title: "Queue Storage trigger missing QueueName",
            messageFormat:
                "[assembly: BenzeneQueueTrigger(Name = {0}, ...)] does not set QueueName. The Queue " +
                "Storage trigger can't be bound without it - set QueueName.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneBlobTrigger(...)]</c> declaration is missing the required
        /// <c>Path</c> (WP-C, #39).
        /// </summary>
        public static readonly DiagnosticDescriptor BlobStorageTriggerMissingPath = new(
            id: "BENZ0007",
            title: "Blob Storage trigger missing Path",
            messageFormat:
                "[assembly: BenzeneBlobTrigger(Name = {0}, ...)] does not set Path. The Blob Storage " +
                "trigger can't be bound without it - set Path.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A trigger attribute explicitly sets <c>Name</c> to <c>""</c>/whitespace-only, across any of
        /// the 9 transports. Distinct from the (correct) absent case, which defaults the name - an
        /// explicitly empty name would emit <c>[Function("")]</c>, an invalid/meaningless Azure
        /// Function name (WP-C, #40).
        /// </summary>
        public static readonly DiagnosticDescriptor EmptyFunctionName = new(
            id: "BENZ0008",
            title: "Empty Azure Function name",
            messageFormat:
                "A Benzene trigger declaration sets Name to {0}, an empty or whitespace-only string. " +
                "Either omit Name to use the default, or give it a non-empty value - Azure Functions " +
                "doesn't accept an empty function name.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneServiceBusTrigger(...)]</c> declaration sets BOTH a queue
        /// (<c>QueueName</c>) and a topic (<c>TopicName</c>/<c>SubscriptionName</c>). The generator
        /// silently prefers the queue and discards the topic - this warns instead of discarding it with
        /// no diagnostic (WP-C, #42). Non-blocking: the trigger is still generated (queue wins, as
        /// before), so this doesn't change behavior, just surfaces it.
        /// </summary>
        public static readonly DiagnosticDescriptor ServiceBusAmbiguousQueueAndTopic = new(
            id: "BENZ0009",
            title: "ServiceBus trigger sets both queue and topic",
            messageFormat:
                "[assembly: BenzeneServiceBusTrigger(Name = {0}, ...)] sets both QueueName and " +
                "TopicName/SubscriptionName. A Service Bus trigger binds to exactly one - QueueName " +
                "was used and the topic was ignored. Set only one to remove this ambiguity.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
    }
}
