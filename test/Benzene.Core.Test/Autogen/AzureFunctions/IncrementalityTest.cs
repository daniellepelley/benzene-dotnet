using System.Linq;
using Benzene.Azure.Function.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Benzene.Test.Autogen.AzureFunctions;

// WP-C, #38's second flagged issue: WP-5 merged every transport into one array feeding a single
// RegisterSourceOutput, so any single trigger edit re-emitted every trigger class - not just the
// edited one. AzureFunctionTriggerGenerator now registers one RegisterSourceOutput per transport
// (collision detection alone stays global - see AzureFunctionTriggerGeneratorTest's #32 tests). The
// per-transport step's own recorded IncrementalStepRunReason - via GeneratorDriverOptions'
// trackIncrementalGeneratorSteps plus WithTrackingName on each transport's combined provider - is
// Roslyn's own authoritative signal for "did this node's callback need to re-run", so that's what this
// asserts, rather than object identity of the AddSource output (which isn't a documented guarantee -
// the driver may re-invoke a step's wrapping combinators for bookkeeping even when the step itself is
// content-unchanged, without that meaning the emitted source actually changed; content equality is
// checked here too, as the behavior that actually matters to a consuming project).
public class IncrementalityTest
{
    private const string StubAttributes = @"
namespace Benzene.Azure.Function.QueueStorage { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneQueueTriggerAttribute : System.Attribute { public string Name {get;set;} public string QueueName {get;set;} public string Connection {get;set;} } }
namespace Benzene.Azure.Function.Kafka { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneKafkaTriggerAttribute : System.Attribute { public string Name {get;set;} public string BrokerList {get;set;} public string Topic {get;set;} public string ConsumerGroup {get;set;} } }
";

    [Fact]
    public void EditingOneTransport_DoesNotRegenerateAnUnrelatedTransportsOutput()
    {
        const string queueDecl = @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""q1"", QueueName = ""qa"")]
";
        const string kafkaDeclV1 = @"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""k1"", Topic = ""orders-v1"")]
";
        const string kafkaDeclV2 = @"[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""k1"", Topic = ""orders-v2"")]
";

        var text1 = SourceText.From(queueDecl + kafkaDeclV1 + StubAttributes);
        var tree1 = CSharpSyntaxTree.ParseText(text1, path: "decls.cs");
        var compilation1 = CSharpCompilation.Create(
            "TestAsm",
            new[] { tree1 },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driverOptions = new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true);
        var driver = CSharpGeneratorDriver.Create(
            new[] { new AzureFunctionTriggerGenerator().AsSourceGenerator() },
            driverOptions: driverOptions);
        driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation1);
        var result1 = driver.GetRunResult();

        var queueTree1 = FindGeneratedTree(result1, "QueueFunction");
        var kafkaTree1 = FindGeneratedTree(result1, "KafkaFunction");
        Assert.Contains(@"""qa""", queueTree1.ToString());
        Assert.Contains(@"""orders-v1""", kafkaTree1.ToString());

        // A real, targeted incremental edit: only the Kafka declaration's Topic argument changes (a
        // substring replace within the SAME line), leaving the QueueStorage declaration's text - and
        // Roslyn's incrementally-reparsed node for it - untouched.
        var editStart = text1.ToString().IndexOf("orders-v1", System.StringComparison.Ordinal);
        var text2 = text1.WithChanges(new TextChange(new TextSpan(editStart, "orders-v1".Length), "orders-v2"));
        var tree2 = tree1.WithChangedText(text2);
        var compilation2 = compilation1.ReplaceSyntaxTree(tree1, tree2);
        Assert.StartsWith(queueDecl + kafkaDeclV2, text2.ToString());

        var driver2 = driver.RunGenerators(compilation2);
        var result2 = driver2.GetRunResult();

        var queueTree2 = FindGeneratedTree(result2, "QueueFunction");
        var kafkaTree2 = FindGeneratedTree(result2, "KafkaFunction");

        // The edited transport's output changed, the unrelated one's didn't.
        Assert.Contains(@"""orders-v2""", kafkaTree2.ToString());
        Assert.Contains(@"""qa""", queueTree2.ToString());

        // The direct proof: Roslyn's own per-step bookkeeping (tagged via WithTrackingName in
        // AzureFunctionTriggerGenerator) shows the untouched transport's combined step as Unchanged/
        // Cached on the second run, never Modified/New - i.e. its RegisterSourceOutput callback did not
        // need to re-run at all, while the edited transport's step did.
        var queueStep = Assert.Single(result2.Results[0].TrackedSteps["queueStorage"]);
        Assert.True(
            queueStep.Outputs.All(o => o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            $"expected queueStorage's step to be Cached/Unchanged, was {string.Join(",", queueStep.Outputs.Select(o => o.Reason))}");

        var kafkaStep = Assert.Single(result2.Results[0].TrackedSteps["kafka"]);
        Assert.Contains(kafkaStep.Outputs, o => o.Reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);
    }

    private static SyntaxTree FindGeneratedTree(GeneratorDriverRunResult result, string hintNameContains) =>
        result.GeneratedTrees.Single(t => t.FilePath.Contains(hintNameContains));
}
