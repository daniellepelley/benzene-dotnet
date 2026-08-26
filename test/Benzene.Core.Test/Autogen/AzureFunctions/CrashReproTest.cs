using System.Linq;
using Benzene.Azure.Function.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Benzene.Test.Autogen.AzureFunctions;

// WP-C, #38 (highest priority): TriggerInfo.Location is excluded from equality (deliberately, for
// incremental cache hits), but a TriggerInfo carrying a Diagnostic.Create-bound Location that no
// longer belongs to the CURRENT Compilation crashes Roslyn's suppression-checking with
// ArgumentException, taking the whole build down on an ordinary, unrelated edit. These tests
// reproduce the exact mechanisms the review found live (see
// work/bug-fix-designs-round7-10-2026-08.md, WP-C): two independently-constructed CSharpCompilations
// run through the same driver, and a genuine single-tree incremental edit via
// SyntaxTree.WithChangedText + Compilation.ReplaceSyntaxTree. Each must run generation to completion
// with no exception AND still produce the expected diagnostics - not merely "not throw".
public class CrashReproTest
{
    private const string StubAttributes = @"
namespace Benzene.Azure.Function.QueueStorage { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneQueueTriggerAttribute : System.Attribute { public string Name {get;set;} public string QueueName {get;set;} public string Connection {get;set;} } }
namespace Benzene.Azure.Function.Kafka { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class BenzeneKafkaTriggerAttribute : System.Attribute { public string Name {get;set;} public string BrokerList {get;set;} public string Topic {get;set;} public string ConsumerGroup {get;set;} } }
";

    private const string Decl =
        @"[assembly: Benzene.Azure.Function.QueueStorage.BenzeneQueueTrigger(Name = ""dup"", QueueName = ""qa"")]
[assembly: Benzene.Azure.Function.Kafka.BenzeneKafkaTrigger(Name = ""dup"", Topic = ""orders"")]
";

    private static CSharpCompilation MakeCompilation(params SyntaxTree[] trees) =>
        CSharpCompilation.Create(
            "TestAsm",
            trees,
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    // Reproduction #1: the same GeneratorDriver instance is re-run against a SECOND, independently
    // constructed Compilation (not derived from the first via incremental edit APIs) that happens to
    // declare structurally-identical triggers - the scenario that first exposed the stale-Location
    // hazard.
    [Fact]
    public void TwoIndependentCompilations_SameDriver_DoesNotCrash()
    {
        var tree1 = CSharpSyntaxTree.ParseText(Decl + "\n" + StubAttributes, path: "decls.cs");
        var driver = CSharpGeneratorDriver.Create(new AzureFunctionTriggerGenerator().AsSourceGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGenerators(MakeCompilation(tree1));
        var result1 = driver.GetRunResult();
        AssertDuplicateNameDiagnostics(result1.Diagnostics);

        var tree2 = CSharpSyntaxTree.ParseText(Decl + "\n" + StubAttributes, path: "decls.cs");
        var ex = Record.Exception(() =>
        {
            var driver2 = driver.RunGenerators(MakeCompilation(tree2));
            var result2 = driver2.GetRunResult();
            AssertDuplicateNameDiagnostics(result2.Diagnostics);
        });

        Assert.Null(ex);
    }

    // Reproduction #2: a genuine single-tree incremental edit. The edit is UNRELATED to the trigger
    // declarations (a trailing comment), so the naive expectation is "nothing about the triggers
    // changes" - but the incremental engine can still discard the freshly-recomputed (valid) TriggerInfo
    // in favor of a content-equal cached one whose Location now points outside the new Compilation.
    [Fact]
    public void SingleTreeIncrementalEdit_UnrelatedChange_DoesNotCrash()
    {
        var text1 = Decl + "\n" + StubAttributes;
        var tree1 = CSharpSyntaxTree.ParseText(text1, path: "decls.cs");
        var compilation1 = MakeCompilation(tree1);

        var driver = CSharpGeneratorDriver.Create(new AzureFunctionTriggerGenerator().AsSourceGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation1);
        var result1 = driver.GetRunResult();
        AssertDuplicateNameDiagnostics(result1.Diagnostics);

        var text2 = text1 + "\n// an unrelated trailing comment - the trigger declarations above are untouched\n";
        var tree2 = tree1.WithChangedText(SourceText.From(text2));
        var compilation2 = compilation1.ReplaceSyntaxTree(tree1, tree2);

        var ex = Record.Exception(() =>
        {
            var driver2 = driver.RunGenerators(compilation2);
            var result2 = driver2.GetRunResult();
            AssertDuplicateNameDiagnostics(result2.Diagnostics);
        });

        Assert.Null(ex);
    }

    // A THIRD, successive round of edits on top of #2, now touching a DIFFERENT file entirely (still
    // nothing to do with the triggers) - repeated incremental rounds is what the live review repro'd
    // against a real incremental build loop, not just one edit.
    [Fact]
    public void MultipleSuccessiveIncrementalEdits_DoNotCrash()
    {
        var text1 = Decl + "\n" + StubAttributes;
        var tree1 = CSharpSyntaxTree.ParseText(text1, path: "decls.cs");
        var otherTree = CSharpSyntaxTree.ParseText("namespace App { public class Unrelated { } }", path: "other.cs");
        var compilation = MakeCompilation(tree1, otherTree);

        var driver = CSharpGeneratorDriver.Create(new AzureFunctionTriggerGenerator().AsSourceGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
        AssertDuplicateNameDiagnostics(driver.GetRunResult().Diagnostics);

        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                var newOtherTree = otherTree.WithChangedText(SourceText.From($"namespace App {{ public class Unrelated{i} {{ }} }}"));
                compilation = compilation.ReplaceSyntaxTree(otherTree, newOtherTree);
                otherTree = newOtherTree;

                driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
                AssertDuplicateNameDiagnostics(driver.GetRunResult().Diagnostics);
            }
        });

        Assert.Null(ex);
    }

    private static void AssertDuplicateNameDiagnostics(System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("BENZ0001", d.Id));
    }
}
