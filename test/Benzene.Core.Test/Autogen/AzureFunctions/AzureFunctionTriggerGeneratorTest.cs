using System.Linq;
using Benzene.Azure.Function.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Benzene.Test.Autogen.AzureFunctions;

// Drives AzureFunctionTriggerGenerator directly (CSharpGeneratorDriver, not the flaky
// Microsoft.CodeAnalysis.Testing harness the message-handler generator test skips). Stub attributes
// stand in for the real Benzene.Azure.Function.* ones so the test needs no Azure SDK packages; the
// generator matches them by metadata name via ForAttributeWithMetadataName. Asserts the emitted
// [Function], binding attribute, and dispatch per transport - the shapes proven end-to-end for
// HTTP/Queue/Service Bus via functions.metadata, locked for all nine here.
public class AzureFunctionTriggerGeneratorTest
{
    // No `using` (assembly attributes must precede a using), so System.* types are fully qualified.
    private const string StubAttributes = @"
namespace Benzene.Azure.Function.AspNet { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneHttpTriggerAttribute : System.Attribute { public string Name {get;set;} public string Route {get;set;} } }
namespace Benzene.Azure.Function.ServiceBus { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneServiceBusTriggerAttribute : System.Attribute { public string Name {get;set;} public string QueueName {get;set;} public string TopicName {get;set;} public string SubscriptionName {get;set;} public string Connection {get;set;} } }
namespace Benzene.Azure.Function.EventHub { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneEventHubTriggerAttribute : System.Attribute { public string Name {get;set;} public string EventHubName {get;set;} public string Connection {get;set;} public string ConsumerGroup {get;set;} } }
namespace Benzene.Azure.Function.Kafka { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneKafkaTriggerAttribute : System.Attribute { public string Name {get;set;} public string BrokerList {get;set;} public string Topic {get;set;} public string ConsumerGroup {get;set;} } }
namespace Benzene.Azure.Function.QueueStorage { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneQueueTriggerAttribute : System.Attribute { public string Name {get;set;} public string QueueName {get;set;} public string Connection {get;set;} } }
namespace Benzene.Azure.Function.BlobStorage { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneBlobTriggerAttribute : System.Attribute { public string Name {get;set;} public string Path {get;set;} public string Connection {get;set;} } }
namespace Benzene.Azure.Function.EventGrid { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneEventGridTriggerAttribute : System.Attribute { public string Name {get;set;} } }
namespace Benzene.Azure.Function.CosmosDb { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneCosmosDbTriggerAttribute : System.Attribute { public string Name {get;set;} public System.Type DocumentType {get;set;} public string DatabaseName {get;set;} public string ContainerName {get;set;} public string Connection {get;set;} public string LeaseContainerName {get;set;} public bool CreateLeaseContainerIfNotExists {get;set;} } }
namespace Benzene.Azure.Function.Timer { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneTimerTriggerAttribute : System.Attribute { public string Name {get;set;} public string Schedule {get;set;} public bool RunOnStartup {get;set;} } }
namespace App { public class OrderDoc { } }
";

    private static string Generate(string declarations) => GenerateResult(declarations).Output;

    private static (string Output, System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics) GenerateResult(string declarations)
    {
        var compilation = CSharpCompilation.Create(
            "TestAsm",
            // Assembly attributes must lexically precede namespace/type declarations, so the
            // declarations come first, then the stub attribute definitions they reference.
            new[] { CSharpSyntaxTree.ParseText(declarations + "\n" + StubAttributes) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var runResult = CSharpGeneratorDriver
            .Create(new AzureFunctionTriggerGenerator().AsSourceGenerator())
            .RunGenerators(compilation)
            .GetRunResult();

        var output = string.Join("\n\n", runResult.GeneratedTrees.Select(t => t.ToString()));
        return (output, runResult.Diagnostics);
    }

    [Fact]
    public void Http_EmitsFunctionRouteAndDispatch()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.AspNet.BenzeneHttpTrigger(Name = ""orders"", Route = ""{*restOfPath}"")]");

        Assert.Contains(@"[global::Microsoft.Azure.Functions.Worker.Function(""orders"")]", output);
        Assert.Contains("global::Microsoft.Azure.Functions.Worker.HttpTrigger(", output);
        Assert.Contains(@"Route = ""{*restOfPath}""", output);
        Assert.Contains("global::Microsoft.AspNetCore.Http.HttpRequest req", output);
        Assert.Contains("global::Benzene.Azure.Function.AspNet.Extensions.HandleHttpRequest(_app, req)", output);
    }

    [Fact]
    public void ServiceBus_Queue_EmitsQueueBindingAndDispatch()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", QueueName = ""orders"", Connection = ""ServiceBusConnection"")]");

        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.ServiceBusTrigger(""orders"", Connection = ""ServiceBusConnection"")", output);
        Assert.Contains("global::Azure.Messaging.ServiceBus.ServiceBusReceivedMessage message, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleServiceBusMessages(_app, cancellationToken, message)", output);
    }

    [Fact]
    public void ServiceBus_Topic_EmitsTopicAndSubscription()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", TopicName = ""audit"", SubscriptionName = ""svc"")]");

        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.ServiceBusTrigger(""audit"", ""svc"", Connection = ""ServiceBusConnection"")", output);
    }

    [Fact]
    public void EventHub_EmitsBindingWithConsumerGroup()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.EventHub.BenzeneEventHubTrigger(Name = ""eh"", EventHubName = ""telemetry"", ConsumerGroup = ""$Default"")]");

        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.EventHubTrigger(""telemetry"", Connection = ""EventHubConnection"", ConsumerGroup = ""$Default"")", output);
        Assert.Contains("global::Azure.Messaging.EventHubs.EventData[] events, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleEventHub(_app, cancellationToken, events)", output);
    }

    [Fact]
    public void Kafka_EmitsBrokerTopicAndRecordArray()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""k"", BrokerList = ""BrokerList"", Topic = ""orders"", ConsumerGroup = ""svc"")]");

        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.KafkaTrigger(""BrokerList"", ""orders"", ConsumerGroup = ""svc"")", output);
        Assert.Contains("global::Benzene.Azure.Function.Kafka.KafkaRecord[] events, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleKafkaEvents(_app, cancellationToken, events)", output);
    }

    [Fact]
    public void Queue_EmitsBindingAndStringParam()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""q"", QueueName = ""orders"")]");

        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.QueueTrigger(""orders"", Connection = ""AzureWebJobsStorage"")", output);
        Assert.Contains("] string messageText, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleQueueMessage(_app, messageText, cancellationToken)", output);
    }

    [Fact]
    public void Blob_EmitsTwoParametersAndNameFirstDispatch()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.BlobStorage.BenzeneBlobTrigger(Name = ""b"", Path = ""incoming/{name}"")]");

        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.BlobTrigger(""incoming/{name}"", Connection = ""AzureWebJobsStorage"")", output);
        Assert.Contains("] byte[] content, string name, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleBlob(_app, name, content, cancellationToken)", output);
    }

    [Fact]
    public void EventGrid_EmitsStringBinding()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.EventGrid.BenzeneEventGridTrigger(Name = ""eg"")]");

        Assert.Contains("[global::Microsoft.Azure.Functions.Worker.EventGridTrigger] string eventJson, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleEventGridEvent(_app, eventJson, cancellationToken)", output);
    }

    [Fact]
    public void CosmosDb_EmitsGenericOverDocumentType()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTrigger(Name = ""c"", DocumentType = typeof(App.OrderDoc), DatabaseName = ""shop"", ContainerName = ""orders"", CreateLeaseContainerIfNotExists = true)]");

        Assert.Contains("databaseName: \"shop\"", output);
        Assert.Contains("containerName: \"orders\"", output);
        Assert.Contains("CreateLeaseContainerIfNotExists = true", output);
        Assert.Contains("global::System.Collections.Generic.IReadOnlyList<global::App.OrderDoc> documents, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleCosmosDbChanges<global::App.OrderDoc>(_app, documents, cancellationToken)", output);
    }

    // BENZ0002: a CosmosDb trigger missing DocumentType used to be silently skipped (the change feed
    // is generic over it, so there's nothing valid to emit) - a declared trigger that's silently NOT
    // generated is the worst outcome, so this must now fail the build with a clear diagnostic instead.
    [Fact]
    public void CosmosDb_WithoutDocumentType_ReportsBENZ0002AndEmitsNothing()
    {
        var (output, diagnostics) = GenerateResult(@"[assembly: Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTrigger(Name = ""c"", DatabaseName = ""shop"", ContainerName = ""orders"")]");

        Assert.DoesNotContain("CosmosDBTrigger", output);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("\"c\"", diagnostic.GetMessage());
    }

    // #259 (BENZ0010): DatabaseName/ContainerName are Cosmos DB's own binding-destination fields -
    // exactly analogous to EventHubName/Topic/QueueName/Path on the sibling transports #39 already
    // validated (BENZ0003-BENZ0007) - and were never validated, unlike every one of those. Before the
    // fix this compiled clean (zero diagnostics) and emitted `databaseName: "", containerName: ""`
    // literally - a change-feed trigger bound to nothing, failing only at Azure host startup.
    [Fact]
    public void CosmosDb_MissingDatabaseNameAndContainerName_ReportsBENZ0010AndEmitsNothing()
    {
        var (output, diagnostics) = GenerateResult(@"[assembly: Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTrigger(Name = ""c"", DocumentType = typeof(App.OrderDoc))]");

        Assert.DoesNotContain("CosmosDBTrigger", output);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0010", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("\"c\"", diagnostic.GetMessage());
    }

    // Missing only ONE of the two must still trip the check - not just "both missing".
    [Theory]
    [InlineData(@"[assembly: Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTrigger(Name = ""c"", DocumentType = typeof(App.OrderDoc), ContainerName = ""orders"")]")]
    [InlineData(@"[assembly: Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTrigger(Name = ""c"", DocumentType = typeof(App.OrderDoc), DatabaseName = ""shop"")]")]
    public void CosmosDb_MissingOnlyOneOfDatabaseNameOrContainerName_ReportsBENZ0010(string declaration)
    {
        var (output, diagnostics) = GenerateResult(declaration);

        Assert.DoesNotContain("CosmosDBTrigger", output);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0010", diagnostic.Id);
    }

    // DocumentType missing takes precedence in reporting only in the sense that both checks run
    // independently - when DocumentType alone is missing (DatabaseName/ContainerName both set), only
    // BENZ0002 fires, not BENZ0010 too, since the DocumentType check returns first.
    [Fact]
    public void CosmosDb_MissingOnlyDocumentType_ReportsOnlyBENZ0002()
    {
        var (_, diagnostics) = GenerateResult(@"[assembly: Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTrigger(Name = ""c"", DatabaseName = ""shop"", ContainerName = ""orders"")]");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0002", diagnostic.Id);
    }

    [Fact]
    public void Timer_EmitsScheduleRunOnStartupAndNoArgDispatch()
    {
        var output = Generate(@"[assembly: Benzene.Azure.Function.Timer.BenzeneTimerTrigger(Name = ""t"", Schedule = ""0 */1 * * * *"", RunOnStartup = true)]");

        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.TimerTrigger(""0 */1 * * * *"", RunOnStartup = true)", output);
        // The bound "timer" parameter is the Azure SDK's TimerInfo, not Benzene's own TimerTriggerInfo
        // - there's no conversion, so (as before this change) it's bound but not forwarded to the
        // dispatch call; only cancellationToken is newly threaded through.
        Assert.Contains("global::Microsoft.Azure.Functions.Worker.TimerInfo timer, global::System.Threading.CancellationToken cancellationToken", output);
        Assert.Contains("HandleTimer(_app, cancellationToken: cancellationToken)", output);
    }

    [Fact]
    public void MultipleDeclarations_EmitOneClassEach()
    {
        var output = Generate(
            @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""a"", QueueName = ""qa"")]" +
            @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""b"", QueueName = ""qb"")]");

        Assert.Contains(@"[global::Microsoft.Azure.Functions.Worker.Function(""a"")]", output);
        Assert.Contains(@"[global::Microsoft.Azure.Functions.Worker.Function(""b"")]", output);
    }

    // BENZ0001: a Function name must be unique across the whole app, checked globally across every
    // transport - not per-transport - because Azure Functions doesn't know or care which binding
    // produced the name. Round 6 proved the collision is cross-transport: a
    // BenzeneQueueTrigger(Name="dup") and a BenzeneKafkaTrigger(Name="dup") in the same compilation
    // collide exactly as two queue triggers named "dup" would.
    [Fact]
    public void DuplicateFunctionName_AcrossDifferentTransports_ReportsBENZ0001AndEmitsNeither()
    {
        var (output, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""dup"", QueueName = ""qa"")]" +
            @"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""dup"", Topic = ""orders"")]");

        // Neither colliding declaration is emitted - which one would be "correct" to keep is exactly
        // the ambiguity the user needs to resolve, so the generator doesn't guess.
        Assert.DoesNotContain(@"[global::Microsoft.Azure.Functions.Worker.Function(""dup"")]", output);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("BENZ0001", d.Id);
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Contains("\"dup\"", d.GetMessage());
        });
    }

    // The ruling explicitly rejects auto-renaming the Function name the way the generated class name
    // is auto-uniquified: the Function name is externally meaningful (bindings, host.json, scale
    // rules, portal identity), so silently picking a different one would just move the failure to
    // deployment. A build-time error, with no renamed name anywhere in the output, is the fix.
    [Fact]
    public void DuplicateFunctionName_IsNeverAutoRenamed()
    {
        var (output, _) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""dup"", QueueName = ""qa"")]" +
            @"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""dup"", Topic = ""orders"")]");

        Assert.DoesNotContain("dup2", output);
    }

    // A distinct, non-colliding trigger declared alongside the duplicates is unaffected - only the
    // colliding pair is withheld.
    [Fact]
    public void DuplicateFunctionName_DoesNotAffectUnrelatedTriggers()
    {
        var (output, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""dup"", QueueName = ""qa"")]" +
            @"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""dup"", Topic = ""orders"")]" +
            @"[assembly: Benzene.Azure.Function.Timer.BenzeneTimerTrigger(Name = ""ok"", Schedule = ""0 */5 * * * *"")]");

        Assert.Contains(@"[global::Microsoft.Azure.Functions.Worker.Function(""ok"")]", output);
        Assert.Equal(2, diagnostics.Length);
    }

    // WP-C, #32: the BENZ0001 collision check used to run AFTER filtering out triggers that carry
    // their own PendingDiagnostic (e.g. a CosmosDb trigger missing DocumentType) - so a collision where
    // one side is broken reported only BENZ0002 and silently shipped the OTHER (valid) trigger under
    // the shared name, with no BENZ0001 at all. The check must now run over the FULL declared set, so
    // both diagnostics fire and NEITHER trigger is emitted.
    [Fact]
    public void DuplicateFunctionName_WhereOneSideHasItsOwnPendingDiagnostic_ReportsBoth()
    {
        var (output, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.CosmosDb.BenzeneCosmosDbTrigger(Name = ""dup"", DatabaseName = ""shop"", ContainerName = ""orders"")]" +
            @"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""dup"", Topic = ""orders"")]");

        Assert.DoesNotContain("CosmosDBTrigger", output);
        Assert.DoesNotContain(@"[global::Microsoft.Azure.Functions.Worker.Function(""dup"")]", output);

        Assert.Contains(diagnostics, d => d.Id == "BENZ0002");
        // The valid (Kafka) side of the collision must not silently ship under the shared name - it
        // gets its own BENZ0001 even though the OTHER side's problem is a different diagnostic.
        Assert.Contains(diagnostics, d => d.Id == "BENZ0001");
    }

    // WP-C, #39: only CosmosDb (BENZ0002) validated its required field; extended to the other five
    // transports with a required binding value. Each must report a build-time diagnostic and emit
    // nothing, instead of silently producing e.g. ServiceBusTrigger("", "").
    [Theory]
    [InlineData(
        @"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", Connection = ""ServiceBusConnection"")]",
        "BENZ0003", "ServiceBusTrigger")]
    [InlineData(
        @"[assembly: Benzene.Azure.Function.EventHub.BenzeneEventHubTrigger(Name = ""eh"")]",
        "BENZ0004", "EventHubTrigger")]
    [InlineData(
        @"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""k"", BrokerList = ""BrokerList"")]",
        "BENZ0005", "KafkaTrigger")]
    [InlineData(
        @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""q"")]",
        "BENZ0006", "QueueTrigger")]
    [InlineData(
        @"[assembly: Benzene.Azure.Function.BlobStorage.BenzeneBlobTrigger(Name = ""b"")]",
        "BENZ0007", "BlobTrigger")]
    public void MissingRequiredField_ReportsDiagnosticAndEmitsNothing(string declaration, string expectedId, string bindingNameThatMustNotAppear)
    {
        var (output, diagnostics) = GenerateResult(declaration);

        Assert.DoesNotContain(bindingNameThatMustNotAppear, output);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(expectedId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    // WP-C, #40: AttributeReading.NamedString couldn't distinguish "Name absent" (correctly defaults)
    // from "Name explicitly set to an empty/whitespace string" (invalid - [Function("")] is meaningless)
    // across all 9 transports. Two representative transports (HTTP - single-attribute providers like
    // it get no positional-argument-driven default path - and a messaging transport) exercise the
    // shared AttributeReading.ValidateName path every transport now calls through.
    [Theory]
    [InlineData(@"[assembly: Benzene.Azure.Function.AspNet.BenzeneHttpTrigger(Name = """")]")]
    [InlineData(@"[assembly: Benzene.Azure.Function.AspNet.BenzeneHttpTrigger(Name = ""   "")]")]
    [InlineData(@"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = """", QueueName = ""qa"")]")]
    public void ExplicitlyEmptyName_ReportsBENZ0008AndEmitsNothing(string declaration)
    {
        var (output, diagnostics) = GenerateResult(declaration);

        Assert.DoesNotContain("global::Microsoft.Azure.Functions.Worker.Function(", output);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0008", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    // An ABSENT Name must still default, not trip the new #40 check - regression guard alongside the
    // explicit-empty cases above.
    [Fact]
    public void AbsentName_StillDefaults_NoDiagnostic()
    {
        var (output, diagnostics) = GenerateResult(@"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(QueueName = ""qa"")]");

        Assert.Empty(diagnostics);
        Assert.Contains(@"[global::Microsoft.Azure.Functions.Worker.Function(""benzene-queue"")]", output);
    }

    // WP-C, #42: setting both QueueName and TopicName/SubscriptionName used to silently prefer the
    // queue and discard the topic with no diagnostic at all. Now warns (BENZ0009) but keeps the same
    // precedence - the trigger is still generated, using the queue.
    [Fact]
    public void ServiceBus_BothQueueAndTopicSet_ReportsBENZ0009ButStillEmitsUsingQueue()
    {
        var (output, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", QueueName = ""orders"", TopicName = ""audit"", SubscriptionName = ""svc"")]");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0009", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        Assert.Contains(@"[global::Microsoft.Azure.Functions.Worker.Function(""sb"")]", output);
        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.ServiceBusTrigger(""orders"", Connection = ""ServiceBusConnection"")", output);
    }

    // Setting only ONE of queue/topic must never trip the new #42 warning - regression guard alongside
    // the existing ServiceBus_Queue_/ServiceBus_Topic_ tests above.
    [Fact]
    public void ServiceBus_OnlyQueueSet_NoAmbiguityDiagnostic()
    {
        var (_, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", QueueName = ""orders"")]");

        Assert.Empty(diagnostics);
    }

    // Round 14-15 #233: TopicName set with SubscriptionName omitted (no QueueName either) passed both
    // BENZ0003 (queue and topic both empty - false, topic is set) and BENZ0009 (queue set - false, no
    // queue) and previously silently generated [ServiceBusTrigger("audit", "")], syntactically valid
    // but broken at deployment. Blocking, like BENZ0003/BENZ0002: nothing is emitted. Renumbered on
    // merge from BENZ0010 to BENZ0011 - round 16/17's independent CosmosDb fix (#259) claimed BENZ0010
    // on main first.
    [Fact]
    public void ServiceBus_TopicWithoutSubscription_ReportsBENZ0011()
    {
        var (output, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", TopicName = ""audit"")]");

        Assert.DoesNotContain("ServiceBusTrigger", output);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0011", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    // The symmetric case: SubscriptionName set with TopicName omitted (no QueueName either). Before
    // this fix, this tripped BENZ0003 ("sets neither QueueName nor TopicName") - technically blocking,
    // but a misleading message that never mentions the SubscriptionName the caller actually set. Now
    // reports the more specific BENZ0011 instead.
    [Fact]
    public void ServiceBus_SubscriptionWithoutTopic_ReportsBENZ0011()
    {
        var (output, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", SubscriptionName = ""svc"")]");

        Assert.DoesNotContain("ServiceBusTrigger", output);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0011", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    // A queue set alongside an asymmetric topic/subscription pair must still take the existing
    // BENZ0009 (ambiguous queue+topic) path, not the new BENZ0011 check - the queue always wins and
    // the topic/subscription pair is discarded wholesale, so its internal (a)symmetry is moot.
    [Fact]
    public void ServiceBus_QueueSetWithTopicButNoSubscription_ReportsBENZ0009NotBENZ0011()
    {
        var (output, diagnostics) = GenerateResult(
            @"[assembly: Benzene.Azure.Function.ServiceBus.BenzeneServiceBusTrigger(Name = ""sb"", QueueName = ""orders"", TopicName = ""audit"")]");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("BENZ0009", diagnostic.Id);
        Assert.Contains(@"global::Microsoft.Azure.Functions.Worker.ServiceBusTrigger(""orders"", Connection = ""ServiceBusConnection"")", output);
    }
}
