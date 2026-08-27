using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Benzene.Test.Contract;

// Guards the 1.0 settlement contract (work/archive/settlement-contract-1.0-2026-07.md) against silent drift between
// the code and docs/capability-matrix.md. The contract is NOT uniform - and the guard encodes that:
//
//  * Queue-shaped transports are safe-by-default: a returned failure result is redelivered
//    (at-least-once), not silently settled. QueueShapedTransports_DefaultToSafe pins each one's *code*
//    default; CapabilityMatrix_MarksQueueTransportSafeByDefault pins its matrix row ("Safe by default").
//  * The two self-hosted *stream* workers (Kafka.Core, Azure.EventHub) default to AT-MOST-ONCE: a
//    stream has no per-message ack, so "don't lose a failed record" would mean halting the whole
//    worker - too drastic to be a default. SelfHostedStreamWorkers_DefaultToAtMostOnce pins their code
//    defaults; CapabilityMatrix_MarksStreamWorkerAtMostOnceByDefault pins their matrix row
//    ("At-most-once by default").
//
// Together they stop the capability matrix from ever again describing a transport's data-safety
// differently from how the code actually behaves - including the awkward stream-worker case where a
// naive reading of RaiseOnFailureStatus=true wrongly suggests safe-by-default.
//
// A THIRD, SEPARATE AXIS is guarded below: the NULL/UNROUTED outcome (no MessageResult recorded -
// overwhelmingly an unrouted message: no handler matched the topic), which is independent of the
// failure-result axis above and is governed by work/settlement-consistency-fix-plan.md §1, not the
// archived 1.0 contract. NullOutcomePolicy_MatchesTheDecidedTable pins the polarity of every adapter's
// enforcement point in source (positive assertions for the Kafka/Event Hub carve-outs included -
// it must fail if one of those is ever "fixed" to retain-on-null); read that document before touching
// any of these lines or the code they assert against.
public class SettlementContractDefaultsTest
{
    [Fact]
    public void QueueShapedTransports_DefaultToSafe()
    {
        // AWS Lambda event sources - a returned failure escalates (RaiseOnFailureStatus) ...
        Assert.True(new Benzene.Aws.Lambda.Sns.SnsOptions().RaiseOnFailureStatus);
        Assert.True(new Benzene.Aws.Lambda.S3.S3Options().RaiseOnFailureStatus);
        Assert.True(new Benzene.Aws.Lambda.EventBridge.EventBridgeOptions().RaiseOnFailureStatus);
        // ... or (Kafka) is reported for redelivery per-partition rather than the whole batch swallowed.
        Assert.Equal(Benzene.Aws.Lambda.Kafka.KafkaBatchFailureMode.PartialBatchFailure,
            new Benzene.Aws.Lambda.Kafka.KafkaOptions().BatchFailureMode);

        // AWS self-hosted SQS consumer - only successfully-handled messages are deleted.
        Assert.Equal(Benzene.Aws.Sqs.Consumer.SqsConsumerAckMode.PerMessage,
            new Benzene.Aws.Sqs.Consumer.SqsConsumerOptions().AckMode);

        // Azure Functions triggers.
        Assert.True(new Benzene.Azure.Function.ServiceBus.ServiceBusOptions().RaiseOnFailureStatus);
        Assert.True(new Benzene.Azure.Function.Kafka.KafkaOptions().RaiseOnFailureStatus);
        Assert.True(new Benzene.Azure.Function.QueueStorage.QueueStorageOptions().RaiseOnFailureStatus);
        Assert.True(new Benzene.Azure.Function.EventGrid.EventGridOptions().RaiseOnFailureStatus);
        Assert.True(new Benzene.Azure.Function.EventHub.Function.EventHubOptions().RaiseOnFailureStatus);

        // Azure self-hosted Service Bus worker (a queue - per-message abandon).
        Assert.Equal(Benzene.Azure.ServiceBus.ServiceBusConsumerAckMode.Explicit,
            new Benzene.Azure.ServiceBus.BenzeneServiceBusConfig().AckMode);

        // RabbitMQ self-hosted worker.
        Assert.Equal(Benzene.RabbitMq.RabbitMqAckMode.Explicit,
            new Benzene.RabbitMq.RabbitMqConfig { QueueName = "guard" }.AckMode);

        // Google Cloud Pub/Sub.
        Assert.True(new Benzene.GoogleCloud.Functions.PubSub.PubSubOptions().RaiseOnFailureStatus);
    }

    [Fact]
    public void SelfHostedStreamWorkers_DefaultToAtMostOnce()
    {
        // A stream has no per-message ack/abandon, so safe-by-default would mean halting the worker
        // (never advancing the offset/checkpoint past a poison record) - too drastic to be a default.
        // Both therefore default to skip-and-continue (at-most-once); at-least-once is opt-in.

        // Event Hub worker: RaiseOnFailureStatus=true escalates a failure result into an exception,
        // but CatchHandlerExceptions=true (this default) then catches it and the partition advances -
        // so the failed event is skipped once a later one checkpoints. At-least-once needs
        // CatchHandlerExceptions=false. Guarding CatchHandlerExceptions here is what pins the
        // *documented* at-most-once default (RaiseOnFailureStatus alone doesn't achieve safety).
        var eventHub = new Benzene.Azure.EventHub.BenzeneEventHubConfig();
        Assert.True(eventHub.CatchHandlerExceptions);
        Assert.True(eventHub.RaiseOnFailureStatus); // true, but defeated by CatchHandlerExceptions above

        // Kafka worker: offsets auto-commit regardless of outcome unless CommitOnlyOnSuccess is set,
        // so a failed record is skipped on restart. At-least-once needs CommitOnlyOnSuccess=true.
        var kafka = new Benzene.Kafka.Core.BenzeneKafkaConfig
        {
            ConsumerConfig = new Confluent.Kafka.ConsumerConfig(),
            Topics = new[] { "guard" }
        };
        Assert.False(kafka.CommitOnlyOnSuccess);
    }

    [Theory]
    [InlineData("Benzene.Aws.Lambda.Sns")]
    [InlineData("Benzene.Aws.Lambda.S3")]
    [InlineData("Benzene.Aws.Lambda.EventBridge")]
    [InlineData("Benzene.Aws.Lambda.Kafka")]
    [InlineData("Benzene.Aws.Sqs")]
    [InlineData("Benzene.Azure.Function.ServiceBus")]
    [InlineData("Benzene.Azure.Function.Kafka")]
    [InlineData("Benzene.Azure.Function.QueueStorage")]
    [InlineData("Benzene.Azure.Function.EventGrid")]
    [InlineData("Benzene.Azure.Function.EventHub")]
    [InlineData("Benzene.Azure.ServiceBus")]
    [InlineData("Benzene.RabbitMq")]
    [InlineData("Benzene.GoogleCloud.Functions.PubSub")]
    public void CapabilityMatrix_MarksQueueTransportSafeByDefault(string packageId) =>
        AssertMatrixRow(packageId, "Safe by default");

    // The two self-hosted stream workers are the deliberate at-most-once exception (see the class
    // comment and the streaming callout in docs/capability-matrix.md).
    [Theory]
    [InlineData("Benzene.Azure.EventHub")]
    [InlineData("Benzene.Kafka.Core")]
    public void CapabilityMatrix_MarksStreamWorkerAtMostOnceByDefault(string packageId) =>
        AssertMatrixRow(packageId, "At-most-once by default");

    // Guards docs/capability-matrix.md's description of the null/unrouted axis (Batch 4 of
    // work/settlement-consistency-fix-plan.md) - every adapter row in the "Retry-on-handler-failure-result"
    // breakdown table must say something about the null-outcome behaviour, not just the failure-result
    // axis, so the doc can't silently fall behind NullOutcomePolicy_MatchesTheDecidedTable below again.
    [Theory]
    [InlineData("Benzene.Aws.Lambda.Sqs")]
    // Benzene.Aws.Lambda.DynamoDb deliberately excluded: its row's own backtick token is unambiguous,
    // but a second, unrelated `Benzene.Aws.Lambda.DynamoDb` mention inside the Event Sourcing area row
    // (a "how to solve the rest" pointer, not a settlement row) makes the single-row-match assumption
    // AssertMatrixRowContains relies on false for this one package id. Covered directly by
    // NullOutcomePolicy_MatchesTheDecidedTable's source-scan instead.
    [InlineData("Benzene.Aws.Lambda.Sns")]
    [InlineData("Benzene.Aws.Lambda.EventBridge")]
    [InlineData("Benzene.Aws.Lambda.Kafka")]
    [InlineData("Benzene.Aws.Lambda.S3")]
    [InlineData("Benzene.Aws.Sqs")]
    [InlineData("Benzene.RabbitMq")]
    [InlineData("Benzene.Azure.Function.ServiceBus")]
    [InlineData("Benzene.Azure.ServiceBus")]
    [InlineData("Benzene.Azure.Function.Kafka")]
    [InlineData("Benzene.Azure.Function.EventGrid")]
    [InlineData("Benzene.Azure.Function.EventHub")]
    [InlineData("Benzene.Azure.Function.QueueStorage")]
    [InlineData("Benzene.GoogleCloud.Functions.PubSub")]
    [InlineData("Benzene.Kafka.Core")]
    [InlineData("Benzene.Azure.EventHub")]
    public void CapabilityMatrix_DescribesNullOutcomeBehavior(string packageId) =>
        AssertMatrixRowContains(packageId, "null/unrouted outcome");

    // Guards the null/unrouted axis of work/settlement-consistency-fix-plan.md §1 (rows 1-18) - a
    // separate axis from the failure-result axis pinned above. Decided policy (maintainer, 2026-08-25):
    // retain/redeliver a null/unestablished outcome wherever a redelivery backstop exists to catch it;
    // ack it only where retaining it would be an unbreakable poison loop (the Kafka x3 / Event Hub x2
    // carve-outs). Each assertion below cites its row number from that document's §1 table; do not add,
    // remove, or "tidy up" one without reading §0 first.
    [Fact]
    public void NullOutcomePolicy_MatchesTheDecidedTable()
    {
        // Rows 1-3: SNS/S3/EventBridge share Benzene.Aws.Lambda.Core's
        // SingleContextEscalatingApplicationBase - blanket flip, no carve-out hook, all three RETAIN.
        var singleContextBase = ReadRepoFile("src/Benzene.Aws.Lambda.Core/SingleContextEscalatingApplicationBase.cs");
        Assert.Contains("context.MessageResult?.IsSuccessful != true", singleContextBase);
        Assert.DoesNotContain("context.MessageResult?.IsSuccessful == false", singleContextBase);

        // Rows 4, 5, 8: QueueStorage/EventGrid/ServiceBus(AutoComplete path) share
        // Benzene.Azure.Function.Core's AzureFunctionBatchApplicationBase. Its EscalateUnestablishedOutcome
        // hook defaults to RETAIN (true); none of the three overrides it back to ack.
        var azureBatchBase = ReadRepoFile("src/Benzene.Azure.Function.Core/AzureFunctionBatchApplicationBase.cs");
        Assert.Contains("protected virtual bool EscalateUnestablishedOutcome => true;", azureBatchBase);
        Assert.DoesNotContain("EscalateUnestablishedOutcome",
            ReadRepoFile("src/Benzene.Azure.Function.QueueStorage/QueueStorageApplication.cs")); // row 4
        Assert.DoesNotContain("EscalateUnestablishedOutcome",
            ReadRepoFile("src/Benzene.Azure.Function.EventGrid/EventGridApplication.cs")); // row 5

        // Row 8 and row 13 share one file (ServiceBusApplication.cs) but are two different enforcement
        // points: the base class's EscalateUnestablishedOutcome-gated guard (AutoComplete path, row 8,
        // inherits the base's RETAIN default - no override) and ServiceBusBatchApplication's own
        // OnPipelineSucceededAsync abandon (Explicit path, row 13, already correct before this plan).
        var serviceBusApplication = ReadRepoFile("src/Benzene.Azure.Function.ServiceBus/ServiceBusApplication.cs");
        Assert.DoesNotContain("EscalateUnestablishedOutcome", serviceBusApplication); // row 8
        Assert.Contains("context.MessageResult?.IsSuccessful != true", serviceBusApplication); // row 13

        // Row 6: Google Cloud Pub/Sub - standalone flip, RETAIN.
        Assert.Contains("context.MessageResult?.IsSuccessful != true",
            ReadRepoFile("src/Benzene.GoogleCloud.Functions.PubSub/PubSubMiddlewareApplication.cs"));

        // Row 7: RabbitMQ worker - standalone flip, RETAIN (nack). Deliberately overturns this package's
        // previously documented+tested ack-on-null behaviour - see the plan's decision register.
        Assert.Contains("messageResult?.IsSuccessful != true",
            ReadRepoFile("src/Benzene.RabbitMq/RabbitMqWorker.cs"));

        // Rows 9-12: already correct before this plan (untouched by Batch 1), RETAIN.
        Assert.Contains("context.MessageResult?.IsSuccessful != true",
            ReadRepoFile("src/Benzene.Aws.Lambda.Sqs/SqsApplication.cs")); // row 9
        Assert.Contains("pair.Context.MessageResult?.IsSuccessful != true",
            ReadRepoFile("src/Benzene.Aws.Sqs/Consumer/SqsConsumerApplication.cs")); // row 10
        Assert.Contains("context.MessageResult?.IsSuccessful != true",
            ReadRepoFile("src/Benzene.Aws.Lambda.DynamoDb/DynamoDbApplication.cs")); // row 11
        Assert.Contains("decision.MessageResult?.IsSuccessful != true",
            ReadRepoFile("src/Benzene.Azure.ServiceBus/BenzeneServiceBusWorker.cs")); // row 12

        // Rows 14-18: CARVE-OUTS - positive assertions that ack-on-null is still there. Each of these
        // must fail if someone "fixes" a carve-out to retain-on-null; no per-record dead-letter path
        // means retaining would replay the partition/batch forever.
        Assert.Contains("context.MessageResult?.IsSuccessful == false",
            ReadRepoFile("src/Benzene.Aws.Lambda.Kafka/KafkaApplication.cs")); // row 14
        Assert.Contains("protected override bool EscalateUnestablishedOutcome => false;",
            ReadRepoFile("src/Benzene.Azure.Function.Kafka/KafkaApplication.cs")); // row 15
        Assert.Contains("messageResult?.IsSuccessful == false",
            ReadRepoFile("src/Benzene.Kafka.Core/BenzeneKafkaWorker.cs")); // row 16
        Assert.Contains("protected override bool EscalateUnestablishedOutcome => false;",
            ReadRepoFile("src/Benzene.Azure.Function.EventHub/Function/EventHubApplication.cs")); // row 17
        Assert.Contains("messageResult?.IsSuccessful == false",
            ReadRepoFile("src/Benzene.Azure.EventHub/BenzeneEventHubWorker.cs")); // row 18
    }

    // Completeness backstop for the theory above: if a *new* class starts extending either shared base
    // (or an existing one stops), that is a new/removed null-outcome policy row work/settlement-consistency-fix-plan.md
    // §1 has not considered - fail loudly here instead of the new adapter silently inheriting whatever
    // the base class default happens to be. Mirrors the grep the plan itself prescribes before editing
    // either base class (see Batch 1, "Before editing either base class, re-run this...").
    [Fact]
    public void NullOutcomePolicy_SharedBaseConsumersAreComplete()
    {
        var repoRoot = FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");

        AssertExactConsumerSet(repoRoot, srcRoot, ": SingleContextEscalatingApplicationBase<", new[]
        {
            "src/Benzene.Aws.Lambda.Sns/SnsApplication.cs",
            "src/Benzene.Aws.Lambda.S3/S3Application.cs",
            "src/Benzene.Aws.Lambda.EventBridge/EventBridgeApplication.cs",
        }, "Benzene.Aws.Lambda.Core.SingleContextEscalatingApplicationBase");

        AssertExactConsumerSet(repoRoot, srcRoot, ": AzureFunctionBatchApplicationBase<", new[]
        {
            "src/Benzene.Azure.Function.QueueStorage/QueueStorageApplication.cs",
            "src/Benzene.Azure.Function.EventGrid/EventGridApplication.cs",
            "src/Benzene.Azure.Function.ServiceBus/ServiceBusApplication.cs",
            "src/Benzene.Azure.Function.Kafka/KafkaApplication.cs",
            "src/Benzene.Azure.Function.EventHub/Function/EventHubApplication.cs",
        }, "Benzene.Azure.Function.Core.AzureFunctionBatchApplicationBase");
    }

    private static void AssertExactConsumerSet(string repoRoot, string srcRoot, string extendsToken, string[] expectedRepoRelative, string baseClassName)
    {
        var actual = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains(extendsToken))
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var expected = expectedRepoRelative.OrderBy(path => path, StringComparer.Ordinal).ToList();

        Assert.True(actual.SequenceEqual(expected),
            $"The set of classes extending {baseClassName} has changed since " +
            "work/settlement-consistency-fix-plan.md's §1 table was written.\n" +
            $"Expected: {string.Join(", ", expected)}\n" +
            $"Actual:   {string.Join(", ", actual)}\n" +
            "A new or removed consumer means a null-outcome policy row that document has not " +
            "considered - stop and report per its §0 rule 1; do not infer the policy from a " +
            "neighbouring adapter.");
    }

    private static string ReadRepoFile(string repoRelativePath)
    {
        var full = Path.Combine(FindRepoRoot(), repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Expected settlement-guard file not found: {repoRelativePath}. If it moved, update " +
                "both this test and work/settlement-consistency-fix-plan.md's §1 table.", full);
        }

        return File.ReadAllText(full);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "work", "settlement-consistency-fix-plan.md")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repo root (work/settlement-consistency-fix-plan.md) walking up from {AppContext.BaseDirectory}");
    }

    private static void AssertMatrixRow(string packageId, string expectedMarker)
    {
        var matrixPath = FindRepoFile(Path.Combine("docs", "capability-matrix.md"));
        var token = $"`{packageId}`";

        // A table row is a line starting with '|'; match on the backtick-wrapped package id so a
        // shorter id can't match a longer one's row (e.g. Benzene.Azure.EventHub vs .Function.EventHub).
        var rows = File.ReadLines(matrixPath)
            .Where(line => line.TrimStart().StartsWith("|") && line.Contains(token))
            .ToList();

        Assert.True(rows.Count == 1,
            $"Expected exactly one capability-matrix.md table row mentioning {token}, found {rows.Count}. " +
            "Add/deduplicate its row (see work/archive/settlement-contract-1.0-2026-07.md).");
        Assert.True(rows[0].Contains(expectedMarker),
            $"The capability-matrix.md row for {token} must say \"{expectedMarker}\" - it has drifted " +
            $"from the code default the settlement contract guarantees. Row:\n{rows[0]}");
    }

    private static void AssertMatrixRowContains(string packageId, string expectedSubstring)
    {
        var matrixPath = FindRepoFile(Path.Combine("docs", "capability-matrix.md"));
        var token = $"`{packageId}`";

        var rows = File.ReadLines(matrixPath)
            .Where(line => line.TrimStart().StartsWith("|") && line.Contains(token))
            .ToList();

        Assert.True(rows.Count == 1,
            $"Expected exactly one capability-matrix.md table row mentioning {token}, found {rows.Count}. " +
            "Add/deduplicate its row (see work/settlement-consistency-fix-plan.md).");
        Assert.True(rows[0].Contains(expectedSubstring),
            $"The capability-matrix.md row for {token} must mention \"{expectedSubstring}\" - it has " +
            $"drifted from the null-outcome policy work/settlement-consistency-fix-plan.md §1 decides. " +
            $"Row:\n{rows[0]}");
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }
}
