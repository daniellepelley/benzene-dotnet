using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Benzene.Azure.Function.SourceGenerators
{
    // One reader per non-HTTP transport. Each turns its assembly attribute into a TriggerInfo whose
    // ParameterList carries the exact [XTrigger] binding + bound parameter(s), and whose dispatch
    // forwards into the matching Benzene IAzureFunctionApp.HandleX(...) extension. Fully qualified
    // (global::) so the generated file needs no usings. Binding/dispatch shapes verified against
    // docs/azure-functions.md and src/Benzene.Azure.Function.*.

    internal static class ServiceBus
    {
        public const string AttributeName = "Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTriggerAttribute";

        public static ImmutableArray<TriggerInfo> Read(GeneratorAttributeSyntaxContext context)
        {
            var builder = ImmutableArray.CreateBuilder<TriggerInfo>();
            foreach (var a in context.Attributes)
            {
                var name = AttributeReading.NamedString(a, "Name", "benzene-service-bus");
                var queue = AttributeReading.NamedString(a, "QueueName", "");
                var topic = AttributeReading.NamedString(a, "TopicName", "");
                var subscription = AttributeReading.NamedString(a, "SubscriptionName", "");
                var connection = AttributeReading.NamedString(a, "Connection", "ServiceBusConnection");

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
                    AttributeReading.AttributeLocation(a)));
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
                var name = AttributeReading.NamedString(a, "Name", "benzene-event-hub");
                var hub = AttributeReading.NamedString(a, "EventHubName", "");
                var connection = AttributeReading.NamedString(a, "Connection", "EventHubConnection");
                var consumerGroup = AttributeReading.NamedString(a, "ConsumerGroup", "");

                var binding = $"global::Microsoft.Azure.Functions.Worker.EventHubTrigger({AttributeReading.Literal(hub)}, Connection = {AttributeReading.Literal(connection)}{AttributeReading.OptionalStringArg("ConsumerGroup", consumerGroup)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "EventHubFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] global::Azure.Messaging.EventHubs.EventData[] events, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.EventHub.Function.Extensions.HandleEventHub(_app, cancellationToken, events)",
                    AttributeReading.AttributeLocation(a)));
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
                var name = AttributeReading.NamedString(a, "Name", "benzene-kafka");
                var brokerList = AttributeReading.NamedString(a, "BrokerList", "BrokerList");
                var topic = AttributeReading.NamedString(a, "Topic", "");
                var consumerGroup = AttributeReading.NamedString(a, "ConsumerGroup", "");

                var binding = $"global::Microsoft.Azure.Functions.Worker.KafkaTrigger({AttributeReading.Literal(brokerList)}, {AttributeReading.Literal(topic)}{AttributeReading.OptionalStringArg("ConsumerGroup", consumerGroup)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "KafkaFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] global::Benzene.Azure.Function.Kafka.KafkaRecord[] events, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.Kafka.Extensions.HandleKafkaEvents(_app, cancellationToken, events)",
                    AttributeReading.AttributeLocation(a)));
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
                var name = AttributeReading.NamedString(a, "Name", "benzene-queue");
                var queue = AttributeReading.NamedString(a, "QueueName", "");
                var connection = AttributeReading.NamedString(a, "Connection", "AzureWebJobsStorage");

                var binding = $"global::Microsoft.Azure.Functions.Worker.QueueTrigger({AttributeReading.Literal(queue)}, Connection = {AttributeReading.Literal(connection)})";

                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "QueueFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] string messageText, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.QueueStorage.Extensions.HandleQueueMessage(_app, messageText, cancellationToken)",
                    AttributeReading.AttributeLocation(a)));
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
                var name = AttributeReading.NamedString(a, "Name", "benzene-blob");
                var path = AttributeReading.NamedString(a, "Path", "");
                var connection = AttributeReading.NamedString(a, "Connection", "AzureWebJobsStorage");

                var binding = $"global::Microsoft.Azure.Functions.Worker.BlobTrigger({AttributeReading.Literal(path)}, Connection = {AttributeReading.Literal(connection)})";

                // Two parameters: the blob content (bound) plus the blob name (from the path's {name} token).
                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "BlobFunction",
                    AttributeReading.Literal(name),
                    $"[{binding}] byte[] content, string name, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.BlobStorage.Extensions.HandleBlob(_app, name, content, cancellationToken)",
                    AttributeReading.AttributeLocation(a)));
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
                var name = AttributeReading.NamedString(a, "Name", "benzene-event-grid");

                // Bind as string (both the Event Grid schema and CloudEvents 1.0 arrive as JSON Benzene parses).
                builder.Add(new TriggerInfo(
                    AttributeReading.ToIdentifier(name) + "EventGridFunction",
                    AttributeReading.Literal(name),
                    "[global::Microsoft.Azure.Functions.Worker.EventGridTrigger] string eventJson, global::System.Threading.CancellationToken cancellationToken",
                    "global::System.Threading.Tasks.Task",
                    "global::Benzene.Azure.Function.EventGrid.Extensions.HandleEventGridEvent(_app, eventJson, cancellationToken)",
                    AttributeReading.AttributeLocation(a)));
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
                var name = AttributeReading.NamedString(a, "Name", "benzene-cosmos");
                var documentType = AttributeReading.NamedType(a, "DocumentType");
                if (documentType == null)
                {
                    // DocumentType is required (the change feed is generic over it). A declared
                    // trigger that's silently *not* generated is the worst outcome, so report BENZ0002
                    // instead of skipping it - see DiagnosticDescriptors.
                    builder.Add(TriggerInfo.ForDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.CosmosDbTriggerMissingDocumentType,
                        AttributeReading.AttributeLocation(a),
                        AttributeReading.Literal(name))));
                    continue;
                }

                var database = AttributeReading.NamedString(a, "DatabaseName", "");
                var container = AttributeReading.NamedString(a, "ContainerName", "");
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
                    AttributeReading.AttributeLocation(a)));
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
                var name = AttributeReading.NamedString(a, "Name", "benzene-timer");
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
                    AttributeReading.AttributeLocation(a)));
            }

            return builder.ToImmutable();
        }
    }
}
