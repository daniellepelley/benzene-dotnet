using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Benzene.Test.Conformance;

/// <summary>
/// Pins every .NET binding's settlement defaults against the language-neutral fixture
/// (<c>transport-metadata-cases.json</c> → <c>settlementDefaults</c>; transport-bindings.md §1,
/// obligation 8): each binding's <b>shape</b> matches the fixture's, and each of its defaults is one
/// that shape allows.
/// </summary>
/// <remarks>
/// <para>
/// The defaults are read from the bindings' own constants (a new options instance), never re-typed
/// here, so flipping a default either stays inside what the shape allows or fails this test. The
/// failed-invocation default is recorded in its three parts - a failure result, no result at all, an
/// uncaught error - because the .NET stream bindings do not settle all three alike (the no-result
/// carve-outs of <c>work/settlement-consistency-fix-plan.md</c>, pinned in source by
/// <c>SettlementContractDefaultsTest.NullOutcomePolicy_MatchesTheDecidedTable</c>); each part must be
/// allowed. The fan-in streams (Kinesis, Cosmos DB) have no per-record result, so their no-result part
/// is <c>n/a</c>.
/// </para>
/// <para>
/// An unmatched topic is settled exactly like a failure result: the router records <c>not-found</c>
/// for it. <c>UseUnmatchedTopicAsIgnored()</c> changes that, but it is an option, off by default, so
/// it is not a default and is not asserted here (the fixture's note says the same).
/// </para>
/// </remarks>
public class SettlementDefaultsConformanceTest
{
    private const string Redeliver = "redeliver";
    private const string SkipAndContinue = "skip-and-continue";
    private const string Acknowledge = "acknowledge";
    private const string NotApplicable = "n/a";

    public sealed record BindingDefaults(
        string Package,
        string FixtureBinding,
        string Shape,
        string OnFailureResult,
        string OnNoResult,
        string OnUncaughtError)
    {
        public string UnmatchedTopic => OnFailureResult;

        public override string ToString() => Package;
    }

    private static string Escalates(bool raiseOnFailureStatus) => raiseOnFailureStatus ? Redeliver : Acknowledge;

    private static string Propagates(bool catchExceptions) => catchExceptions ? Acknowledge : Redeliver;

    /// <summary>
    /// Every inbound binding this port ships, mapped to its fixture binding id. A binding with no
    /// fixture id is listed in <see cref="BindingsWithoutAFixtureId"/> instead.
    /// </summary>
    public static IReadOnlyList<BindingDefaults> Bindings()
    {
        var lambdaSqs = new Benzene.Aws.Lambda.Sqs.SqsOptions().BatchFailureMode == Benzene.Aws.Lambda.Sqs.SqsBatchFailureMode.AtMostOnce ? Acknowledge : Redeliver;
        var sqsConsumer = new Benzene.Aws.Sqs.Consumer.SqsConsumerOptions().AckMode switch
        {
            Benzene.Aws.Sqs.Consumer.SqsConsumerAckMode.PerMessage => Redeliver,
            _ => Acknowledge,
        };
        var sqsConsumerError = new Benzene.Aws.Sqs.Consumer.SqsConsumerOptions().AckMode == Benzene.Aws.Sqs.Consumer.SqsConsumerAckMode.AtMostOnce ? Acknowledge : Redeliver;
        var serviceBusFunction = Escalates(new Benzene.Azure.Function.ServiceBus.ServiceBusOptions().RaiseOnFailureStatus);
        var serviceBusWorker = new Benzene.Azure.ServiceBus.BenzeneServiceBusConfig().AckMode == Benzene.Azure.ServiceBus.ServiceBusConsumerAckMode.Explicit ? Redeliver : Acknowledge;
        var serviceBusWorkerError = new Benzene.Azure.ServiceBus.BenzeneServiceBusConfig().AckMode == Benzene.Azure.ServiceBus.ServiceBusConsumerAckMode.AtMostOnce ? Acknowledge : Redeliver;
        var rabbitMq = new Benzene.RabbitMq.RabbitMqConfig { QueueName = "settlement" }.AckMode == Benzene.RabbitMq.RabbitMqAckMode.Explicit ? Redeliver : Acknowledge;
        var lambdaKafka = new Benzene.Aws.Lambda.Kafka.KafkaOptions().BatchFailureMode == Benzene.Aws.Lambda.Kafka.KafkaBatchFailureMode.AtMostOnce ? SkipAndContinue : Redeliver;
        var kafkaWorker = new Benzene.Kafka.Core.BenzeneKafkaConfig { ConsumerConfig = new Confluent.Kafka.ConsumerConfig(), Topics = new[] { "settlement" } };
        var kafkaWorkerDefault = kafkaWorker.CommitOnlyOnSuccess && !kafkaWorker.CatchHandlerExceptions ? Redeliver : SkipAndContinue;
        var eventHubWorker = new Benzene.Azure.EventHub.BenzeneEventHubConfig().CatchHandlerExceptions ? SkipAndContinue : Redeliver;
        var kinesis = new Benzene.Aws.Lambda.Kinesis.KinesisStreamOptions().AtMostOnce ? SkipAndContinue : Redeliver;
        var dynamoDb = new Benzene.Aws.Lambda.DynamoDb.DynamoDbOptions().AtMostOnce ? SkipAndContinue : Redeliver;
        var cosmosFunction = new Benzene.Azure.Function.CosmosDb.CosmosDbChangeFeedOptions().AtMostOnce ? SkipAndContinue : Redeliver;
        var cosmosWorker = new Benzene.Azure.CosmosDb.BenzeneCosmosChangeFeedConfig().CatchHandlerExceptions ? SkipAndContinue : Redeliver;
        var blob = new Benzene.Azure.Function.BlobStorage.BlobStorageOptions().AtMostOnce ? Acknowledge : Redeliver;
        var cosmosAllVersionsWorker = new Benzene.Azure.CosmosDb.BenzeneCosmosAllVersionsChangeFeedConfig().CatchHandlerExceptions ? SkipAndContinue : Redeliver;

        return new[]
        {
            // queue
            new BindingDefaults("Benzene.Aws.Lambda.Sqs", "sqs", "queue", lambdaSqs, lambdaSqs, lambdaSqs),
            new BindingDefaults("Benzene.Aws.Sqs", "sqs", "queue", sqsConsumer, sqsConsumer, sqsConsumerError),
            new BindingDefaults("Benzene.Azure.Function.ServiceBus", "servicebus", "queue", serviceBusFunction, serviceBusFunction,
                Propagates(new Benzene.Azure.Function.ServiceBus.ServiceBusOptions().CatchExceptions)),
            new BindingDefaults("Benzene.Azure.ServiceBus", "servicebus", "queue", serviceBusWorker, serviceBusWorker, serviceBusWorkerError),
            new BindingDefaults("Benzene.Azure.Function.QueueStorage", "queue-storage", "queue",
                Escalates(new Benzene.Azure.Function.QueueStorage.QueueStorageOptions().RaiseOnFailureStatus),
                Escalates(new Benzene.Azure.Function.QueueStorage.QueueStorageOptions().RaiseOnFailureStatus),
                Propagates(new Benzene.Azure.Function.QueueStorage.QueueStorageOptions().CatchExceptions)),
            new BindingDefaults("Benzene.GoogleCloud.Functions.PubSub", "pubsub", "queue",
                Escalates(new Benzene.GoogleCloud.Functions.PubSub.PubSubOptions().RaiseOnFailureStatus),
                Escalates(new Benzene.GoogleCloud.Functions.PubSub.PubSubOptions().RaiseOnFailureStatus),
                Propagates(new Benzene.GoogleCloud.Functions.PubSub.PubSubOptions().CatchExceptions)),
            new BindingDefaults("Benzene.RabbitMq", "rabbitmq", "queue", rabbitMq, rabbitMq, rabbitMq),

            // one-shot
            new BindingDefaults("Benzene.Aws.Lambda.Sns", "sns", "one-shot",
                Escalates(new Benzene.Aws.Lambda.Sns.SnsOptions().RaiseOnFailureStatus),
                Escalates(new Benzene.Aws.Lambda.Sns.SnsOptions().RaiseOnFailureStatus),
                Propagates(new Benzene.Aws.Lambda.Sns.SnsOptions().CatchExceptions)),
            new BindingDefaults("Benzene.Aws.Lambda.EventBridge", "eventbridge", "one-shot",
                Escalates(new Benzene.Aws.Lambda.EventBridge.EventBridgeOptions().RaiseOnFailureStatus),
                Escalates(new Benzene.Aws.Lambda.EventBridge.EventBridgeOptions().RaiseOnFailureStatus),
                Propagates(new Benzene.Aws.Lambda.EventBridge.EventBridgeOptions().CatchExceptions)),
            new BindingDefaults("Benzene.Aws.Lambda.S3", "s3", "one-shot",
                Escalates(new Benzene.Aws.Lambda.S3.S3Options().RaiseOnFailureStatus),
                Escalates(new Benzene.Aws.Lambda.S3.S3Options().RaiseOnFailureStatus),
                Propagates(new Benzene.Aws.Lambda.S3.S3Options().CatchExceptions)),
            new BindingDefaults("Benzene.Azure.Function.EventGrid", "event-grid", "one-shot",
                Escalates(new Benzene.Azure.Function.EventGrid.EventGridOptions().RaiseOnFailureStatus),
                Escalates(new Benzene.Azure.Function.EventGrid.EventGridOptions().RaiseOnFailureStatus),
                Propagates(new Benzene.Azure.Function.EventGrid.EventGridOptions().CatchExceptions)),
            // Not message-routed: no result, only a throw - so a failure is an uncaught error.
            new BindingDefaults("Benzene.Azure.Function.BlobStorage", "blob-storage", "one-shot", blob, NotApplicable, blob),
            new BindingDefaults("Benzene.Azure.Function.Timer", "timer", "one-shot",
                Escalates(new Benzene.Azure.Function.Timer.TimerOptions().RaiseOnFailureStatus),
                Escalates(new Benzene.Azure.Function.Timer.TimerOptions().RaiseOnFailureStatus),
                Propagates(new Benzene.Azure.Function.Timer.TimerOptions().CatchExceptions)),

            // stream - a no-result outcome is skipped on the Kafka/Event Hub carve-outs
            new BindingDefaults("Benzene.Aws.Lambda.Kafka", "kafka", "stream", lambdaKafka, SkipAndContinue, lambdaKafka),
            new BindingDefaults("Benzene.Azure.Function.Kafka", "kafka", "stream",
                new Benzene.Azure.Function.Kafka.KafkaOptions().RaiseOnFailureStatus ? Redeliver : SkipAndContinue,
                SkipAndContinue,
                new Benzene.Azure.Function.Kafka.KafkaOptions().CatchExceptions ? SkipAndContinue : Redeliver),
            new BindingDefaults("Benzene.Kafka.Core", "kafka", "stream", kafkaWorkerDefault, SkipAndContinue, kafkaWorkerDefault),
            new BindingDefaults("Benzene.Azure.Function.EventHub", "eventhub", "stream",
                new Benzene.Azure.Function.EventHub.Function.EventHubOptions().RaiseOnFailureStatus ? Redeliver : SkipAndContinue,
                SkipAndContinue,
                new Benzene.Azure.Function.EventHub.Function.EventHubOptions().CatchExceptions ? SkipAndContinue : Redeliver),
            new BindingDefaults("Benzene.Azure.EventHub", "eventhub", "stream", eventHubWorker, SkipAndContinue, eventHubWorker),
            new BindingDefaults("Benzene.Aws.Lambda.Kinesis", "kinesis", "stream", kinesis, NotApplicable, kinesis),
            new BindingDefaults("Benzene.Aws.Lambda.DynamoDb", "dynamodb-streams", "stream", dynamoDb, dynamoDb, dynamoDb),
            new BindingDefaults("Benzene.Azure.Function.CosmosDb", "cosmosdb-changefeed", "stream", cosmosFunction, NotApplicable, cosmosFunction),
            new BindingDefaults("Benzene.Azure.CosmosDb", "cosmosdb-changefeed", "stream", cosmosWorker, NotApplicable, cosmosWorker),
            new BindingDefaults("Benzene.Azure.CosmosDb (all versions and deletes)", "cosmosdb-changefeed", "stream", cosmosAllVersionsWorker, NotApplicable, cosmosAllVersionsWorker),

            // request-response - settles by responding
            new BindingDefaults("Benzene.AspNet.Core", "http", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.SelfHost", "http", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.Aws.Lambda.ApiGateway", "http", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.Azure.Function.AspNet", "http", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.GoogleCloud.Functions.Http", "http", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.Grpc", "grpc", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.Core.MessageHandlers (BenzeneMessage)", "envelope", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.Aws.Lambda.Core (direct invoke)", "envelope", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.Http (envelope endpoint)", "envelope", "request-response", NotApplicable, NotApplicable, NotApplicable),
            new BindingDefaults("Benzene.Clients.InProcess", "envelope", "request-response", NotApplicable, NotApplicable, NotApplicable),
        };
    }

    /// <summary>
    /// Inbound bindings this port ships that the fixture does not name (README: "a binding a port
    /// ships that the list does not name is added here before the port claims it"). Reported, not
    /// claimed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BindingsWithoutAFixtureId = new Dictionary<string, string>
    {
        ["Benzene.Mcp"] =
            "MCP tool calls over Streamable HTTP or stdio (Provisional) - request/response, but the fixture has no mcp binding",
    };

    /// <summary>
    /// Fixture bindings this port does not ship.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FixtureBindingsNotShipped = new Dictionary<string, string>
    {
        ["envelope-async"] =
            "no asynchronous (one-shot) envelope entry point: the Lambda direct-invoke envelope handler is request/response only, so an Event invocation's failure envelope is returned, not raised",
    };

    private static JsonElement SettlementDefaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conformance", "transport-metadata-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("settlementDefaults").Clone();
    }

    private static string FixtureShape(string binding) =>
        SettlementDefaults().GetProperty("bindings").EnumerateArray()
            .Single(x => x.GetProperty("binding").GetString() == binding)
            .GetProperty("shape").GetString()!;

    private static string[] Allowed(string shape, string member) =>
        SettlementDefaults().GetProperty("shapes").GetProperty(shape).GetProperty(member)
            .EnumerateArray().Select(x => x.GetString()!).ToArray();

    public static IEnumerable<object[]> BindingRows() => Bindings().Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(BindingRows))]
    public void BindingShape_MatchesTheFixture(BindingDefaults binding)
    {
        Assert.Equal(FixtureShape(binding.FixtureBinding), binding.Shape);
    }

    [Theory]
    [MemberData(nameof(BindingRows))]
    public void BindingDefaults_AreOnesTheShapeAllows(BindingDefaults binding)
    {
        var onFailure = Allowed(binding.Shape, "onFailure");
        Assert.True(onFailure.Contains(binding.OnFailureResult),
            $"{binding}: a failure result defaults to '{binding.OnFailureResult}', which a {binding.Shape} may not ({string.Join(", ", onFailure)})");
        Assert.True(onFailure.Contains(binding.OnUncaughtError),
            $"{binding}: an uncaught error defaults to '{binding.OnUncaughtError}', which a {binding.Shape} may not ({string.Join(", ", onFailure)})");
        if (binding.OnNoResult != NotApplicable || binding.Shape == "request-response")
        {
            Assert.True(onFailure.Contains(binding.OnNoResult),
                $"{binding}: no result defaults to '{binding.OnNoResult}', which a {binding.Shape} may not ({string.Join(", ", onFailure)})");
        }

        var unmatched = Allowed(binding.Shape, "unmatchedTopic");
        Assert.True(unmatched.Contains(binding.UnmatchedTopic),
            $"{binding}: an unmatched topic defaults to '{binding.UnmatchedTopic}', which a {binding.Shape} may not ({string.Join(", ", unmatched)})");
    }

    [Fact]
    public void EveryFixtureBinding_IsShippedOrAccountedFor()
    {
        var mapped = Bindings().Select(x => x.FixtureBinding).ToHashSet();
        var fixtureBindings = SettlementDefaults().GetProperty("bindings").EnumerateArray()
            .Select(x => x.GetProperty("binding").GetString()!).ToArray();

        Assert.All(fixtureBindings, binding => Assert.True(
            mapped.Contains(binding) || FixtureBindingsNotShipped.ContainsKey(binding),
            $"Fixture binding '{binding}' is neither mapped to a .NET binding nor listed as not shipped"));
        Assert.All(FixtureBindingsNotShipped.Keys, binding => Assert.Contains(binding, fixtureBindings));
    }

    // transport-bindings.md §1, obligation 8: a stream binding that defaults to skip-and-continue MUST
    // document that it does. The capability matrix row is that documentation.
    [Theory]
    [MemberData(nameof(BindingRows))]
    public void SkipAndContinueDefaults_AreDocumented(BindingDefaults binding)
    {
        if (binding.OnFailureResult != SkipAndContinue && binding.OnNoResult != SkipAndContinue && binding.OnUncaughtError != SkipAndContinue)
        {
            return;
        }

        var package = binding.Package.Split(' ')[0];
        var row = MatrixRow(package);
        Assert.True(row.Contains("skip-and-continue", StringComparison.OrdinalIgnoreCase),
            $"docs/capability-matrix.md's row for `{package}` must say it defaults to skip-and-continue. Row:\n{row}");
    }

    private static string MatrixRow(string package)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "capability-matrix.md");
            if (File.Exists(candidate))
            {
                var token = $"`{package}`";
                var rows = File.ReadLines(candidate)
                    .Where(line => line.TrimStart().StartsWith("|") && line.Contains(token))
                    .ToList();
                Assert.True(rows.Count >= 1, $"No capability-matrix.md row mentions {token}");
                return string.Join("\n", rows);
            }
        }

        throw new FileNotFoundException("docs/capability-matrix.md not found walking up from " + AppContext.BaseDirectory);
    }
}
