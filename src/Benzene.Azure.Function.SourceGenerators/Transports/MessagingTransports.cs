using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Benzene.Azure.Function.SourceGenerators
{
    // One reader per non-HTTP transport. Each turns its assembly attribute into a TriggerInfo whose
    // ParameterList carries the exact [XTrigger] binding + bound parameter(s), and whose dispatch
    // forwards into the matching Benzene IAzureFunctionApp.HandleX(...) extension. Fully qualified
    // (global::) so the generated file needs no usings. Binding/dispatch shapes verified against
    // docs/azure-functions.md and src/Benzene.Azure.Function.*.
    //
    // Every reader validates Name first (AttributeReading.ValidateName - BENZ0008, WP-C #40), then its
    // own required field(s) (BENZ0002-BENZ0007, WP-C #39) before building a binding: a reader that
    // can't produce a valid trigger reports a blocking diagnostic (TriggerInfo.ForDiagnostic) instead
    // of silently emitting an invalid/empty binding argument.

    internal static class ServiceBus
    {
        public const string AttributeName = "Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-service-bus", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                var queue = AttributeReading.NamedString(a, "QueueName", "");
                var topic = AttributeReading.NamedString(a, "TopicName", "");
                var subscription = AttributeReading.NamedString(a, "SubscriptionName", "");
                var connection = AttributeReading.NamedString(a, "Connection", "ServiceBusConnection");

                // #39 (BENZ0003): neither a queue nor a topic set - nothing to bind to.
                if (queue.Length == 0 && topic.Length == 0)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(
                        AttributeReading.Literal(name),
                        location,
                        new PendingDiagnosticInfo(DiagnosticDescriptors.ServiceBusTriggerMissingDestination, AttributeReading.Literal(name))));
                    continue;
                }

                // #42 (BENZ0009): both set - queue wins (unchanged behavior), but this used to discard
                // the topic with no diagnostic at all. Warn, don't block: the trigger below is still
                // generated using the same precedence as before.
                var advisories = ImmutableArray<PendingDiagnosticInfo>.Empty;
                if (queue.Length > 0 && (topic.Length > 0 || subscription.Length > 0))
                {
                    advisories = ImmutableArray.Create(
                        new PendingDiagnosticInfo(DiagnosticDescriptors.ServiceBusAmbiguousQueueAndTopic, AttributeReading.Literal(name)));
                }

                // Queue trigger takes one positional (queue); topic trigger takes two (topic, subscription).
                var entity = queue.Length > 0
                    ? AttributeReading.Literal(queue)
                    : $"{AttributeReading.Literal(topic)}, {AttributeReading.Literal(subscription)}";
                var binding = $"global::Microsoft.Azure.Functions.Worker.ServiceBusTrigger({entity}, Connection = {AttributeReading.Literal(connection)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "ServiceBusFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] global::Azure.Messaging.ServiceBus.ServiceBusReceivedMessage message, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.ServiceBus.Extensions.HandleServiceBusMessages(_app, cancellationToken, message)",
                    location,
                    advisories));
            }

            return builder.ToImmutable();
        }
    }

    internal static class EventHub
    {
        public const string AttributeName = "Benzene.Azure.Function.EventHub.BenzeneEventHubTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-event-hub", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                var hub = AttributeReading.NamedString(a, "EventHubName", "");
                var connection = AttributeReading.NamedString(a, "Connection", "EventHubConnection");
                var consumerGroup = AttributeReading.NamedString(a, "ConsumerGroup", "");

                // #39 (BENZ0004): EventHubName required.
                if (hub.Length == 0)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(
                        AttributeReading.Literal(name),
                        location,
                        new PendingDiagnosticInfo(DiagnosticDescriptors.EventHubTriggerMissingEventHubName, AttributeReading.Literal(name))));
                    continue;
                }

                var binding = $"global::Microsoft.Azure.Functions.Worker.EventHubTrigger({AttributeReading.Literal(hub)}, Connection = {AttributeReading.Literal(connection)}{AttributeReading.OptionalStringArg("ConsumerGroup", consumerGroup)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "EventHubFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] global::Azure.Messaging.EventHubs.EventData[] events, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.EventHub.Function.Extensions.HandleEventHub(_app, cancellationToken, events)",
                    location));
            }

            return builder.ToImmutable();
        }
    }

    internal static class Kafka
    {
        public const string AttributeName = "Benzene.Azure.Function.Kafka.BenzeneKafkaTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-kafka", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                var brokerList = AttributeReading.NamedString(a, "BrokerList", "BrokerList");
                var topic = AttributeReading.NamedString(a, "Topic", "");
                var consumerGroup = AttributeReading.NamedString(a, "ConsumerGroup", "");

                // #39 (BENZ0005): Topic required.
                if (topic.Length == 0)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(
                        AttributeReading.Literal(name),
                        location,
                        new PendingDiagnosticInfo(DiagnosticDescriptors.KafkaTriggerMissingTopic, AttributeReading.Literal(name))));
                    continue;
                }

                var binding = $"global::Microsoft.Azure.Functions.Worker.KafkaTrigger({AttributeReading.Literal(brokerList)}, {AttributeReading.Literal(topic)}{AttributeReading.OptionalStringArg("ConsumerGroup", consumerGroup)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "KafkaFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] global::Benzene.Azure.Function.Kafka.KafkaRecord[] events, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.Kafka.Extensions.HandleKafkaEvents(_app, cancellationToken, events)",
                    location));
            }

            return builder.ToImmutable();
        }
    }

    internal static class QueueStorage
    {
        public const string AttributeName = "Benzene.Azure.Function.QueueStorage.BenzeneQueueTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-queue", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                var queue = AttributeReading.NamedString(a, "QueueName", "");
                var connection = AttributeReading.NamedString(a, "Connection", "AzureWebJobsStorage");

                // #39 (BENZ0006): QueueName required.
                if (queue.Length == 0)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(
                        AttributeReading.Literal(name),
                        location,
                        new PendingDiagnosticInfo(DiagnosticDescriptors.QueueStorageTriggerMissingQueueName, AttributeReading.Literal(name))));
                    continue;
                }

                var binding = $"global::Microsoft.Azure.Functions.Worker.QueueTrigger({AttributeReading.Literal(queue)}, Connection = {AttributeReading.Literal(connection)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "QueueFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] string messageText, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.QueueStorage.Extensions.HandleQueueMessage(_app, messageText, cancellationToken)",
                    location));
            }

            return builder.ToImmutable();
        }
    }

    internal static class BlobStorage
    {
        public const string AttributeName = "Benzene.Azure.Function.BlobStorage.BenzeneBlobTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-blob", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                var path = AttributeReading.NamedString(a, "Path", "");
                var connection = AttributeReading.NamedString(a, "Connection", "AzureWebJobsStorage");

                // #39 (BENZ0007): Path required.
                if (path.Length == 0)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(
                        AttributeReading.Literal(name),
                        location,
                        new PendingDiagnosticInfo(DiagnosticDescriptors.BlobStorageTriggerMissingPath, AttributeReading.Literal(name))));
                    continue;
                }

                var binding = $"global::Microsoft.Azure.Functions.Worker.BlobTrigger({AttributeReading.Literal(path)}, Connection = {AttributeReading.Literal(connection)})";

                // Two parameters: the blob content (bound) plus the blob name (from the path's {name} token).
                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "BlobFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] byte[] content, string name, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.BlobStorage.Extensions.HandleBlob(_app, name, content, cancellationToken)",
                    location));
            }

            return builder.ToImmutable();
        }
    }

    internal static class EventGrid
    {
        public const string AttributeName = "Benzene.Azure.Function.EventGrid.BenzeneEventGridTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-event-grid", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                // Bind as string (both the Event Grid schema and CloudEvents 1.0 arrive as JSON Benzene parses).
                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "EventGridFunction",
                    AttributeReading.Literal(name),
                    "[global::Microsoft.Azure.Functions.Worker.EventGridTrigger] string eventJson, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.EventGrid.Extensions.HandleEventGridEvent(_app, eventJson, cancellationToken)",
                    location));
            }

            return builder.ToImmutable();
        }
    }

    internal static class CosmosDb
    {
        public const string AttributeName = "Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-cosmos", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                var documentType = AttributeReading.NamedType(a, "DocumentType");
                if (documentType == null)
                {
                    // DocumentType is required (the change feed is generic over it). A declared
                    // trigger that's silently *not* generated is the worst outcome, so report BENZ0002
                    // instead of skipping it - see DiagnosticDescriptors.
                    builder.Add(TriggerInfo.ForDiagnostic(
                        AttributeReading.Literal(name),
                        location,
                        new PendingDiagnosticInfo(DiagnosticDescriptors.CosmosDbTriggerMissingDocumentType, AttributeReading.Literal(name))));
                    continue;
                }

                var database = AttributeReading.NamedString(a, "DatabaseName", "");
                var container = AttributeReading.NamedString(a, "ContainerName", "");

                // #259 (BENZ0010): DatabaseName/ContainerName are Cosmos DB's own binding-destination
                // fields - exactly analogous to EventHubName/Topic/QueueName/Path on the sibling
                // transports (BENZ0003-BENZ0007) - and were never validated, unlike every one of those.
                // Checked alongside (not instead of) the DocumentType/BENZ0002 check above.
                if (database.Length == 0 || container.Length == 0)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(
                        AttributeReading.Literal(name),
                        location,
                        new PendingDiagnosticInfo(DiagnosticDescriptors.CosmosDbTriggerMissingDestination, AttributeReading.Literal(name))));
                    continue;
                }

                var connection = AttributeReading.NamedString(a, "Connection", "CosmosDbConnection");
                var lease = AttributeReading.NamedString(a, "LeaseContainerName", "leases");
                var createLease = AttributeReading.NamedBool(a, "CreateLeaseContainerIfNotExists", false);

                var binding =
                    "global::Microsoft.Azure.Functions.Worker.CosmosDBTrigger("
                    + $"databaseName: {AttributeReading.Literal(database)}, "
                    + $"containerName: {AttributeReading.Literal(container)}, "
                    + $"Connection = {AttributeReading.Literal(connection)}, "
                    + $"LeaseContainerName = {AttributeReading.Literal(lease)}"
                    + AttributeReading.OptionalBoolArg("CreateLeaseContainerIfNotExists", createLease)
                    + ")";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "CosmosDbFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] global::System.Collections.Generic.IReadOnlyList<{documentType}> documents, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    $"global::Benzene.Azure.Function.CosmosDb.Extensions.HandleCosmosDbChanges<{documentType}>(_app, documents, cancellationToken)",
                    location));
            }

            return builder.ToImmutable();
        }
    }

    internal static class Timer
    {
        public const string AttributeName = "Benzene.Azure.Function.Timer.BenzeneTimerTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var location = AttributeReading.AttributeLocation(a);

                var emptyName = AttributeReading.ValidateName(a, "benzene-timer", out var name);
                if (emptyName is { } emptyNameDiagnostic)
                {
                    builder.Add(TriggerInfo.ForDiagnostic(AttributeReading.Literal(name), location, emptyNameDiagnostic));
                    continue;
                }

                var schedule = AttributeReading.NamedString(a, "Schedule", "0 */5 * * * *");
                var runOnStartup = AttributeReading.NamedBool(a, "RunOnStartup", false);

                var binding = $"global::Microsoft.Azure.Functions.Worker.TimerTrigger({AttributeReading.Literal(schedule)}{AttributeReading.OptionalBoolArg("RunOnStartup", runOnStartup)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "TimerFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] global::Microsoft.Azure.Functions.Worker.TimerInfo timer, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    // The bound "timer" parameter is the Azure SDK's TimerInfo, not Benzene's own
                    // TimerTriggerInfo - there's no conversion, so (as before this change) it's bound
                    // but intentionally not forwarded; only cancellationToken is new here.
                    "global::Benzene.Azure.Function.Timer.Extensions.HandleTimer(_app, cancellationToken: cancellationToken)",
                    location));
            }

            return builder.ToImmutable();
        }
    }
}
