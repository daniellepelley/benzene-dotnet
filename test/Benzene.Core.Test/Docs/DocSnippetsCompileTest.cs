using System.Linq;
using Xunit;

namespace Benzene.Test.Docs;

/// <summary>
/// Every documentation snippet marked <c>&lt;!-- compile: … --&gt;</c> must build.
/// </summary>
/// <remarks>
/// See <see cref="DocSnippetCompiler"/> for why this is opt-in per block, and for what it does and
/// does not prove. Coverage is deliberately partial and visibly so: the count assertion below fails
/// if the directives disappear, so the check cannot quietly stop covering anything.
/// </remarks>
public class DocSnippetsCompileTest
{
    public static TheoryData<DocSnippet> Snippets()
    {
        var data = new TheoryData<DocSnippet>();
        foreach (var snippet in DocSnippetCompiler.Discover())
        {
            data.Add(snippet);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Snippets))]
    public void ASnippetTheDocsPresentAsCompleteCodeCompiles(DocSnippet snippet)
    {
        var errors = DocSnippetCompiler.Compile(snippet);

        Assert.True(errors.Count == 0,
            $"{snippet} does not compile as written:\n  " +
            string.Join("\n  ", errors.Select(x =>
            {
                var span = x.Location.GetLineSpan();
                return $"{span.Path}:{span.StartLinePosition.Line + 1} {x.Id} {x.GetMessage()}";
            })));
    }

    [Fact]
    public void TheGettingStartedGuidesAreAllCovered()
    {
        // A guide losing its directive would silently drop out of the theory above, and the check
        // would go green by covering nothing. Name them.
        var covered = DocSnippetCompiler.Discover().Select(x => x.DocPath).Distinct().ToArray();

        foreach (var guide in new[]
                 {
                     "docs/getting-started-aspnet.md",
                     "docs/getting-started-aws.md",
                     "docs/getting-started-kafka.md",
                     "docs/getting-started-rabbitmq.md",
                     "docs/azure-functions.md"
                 })
        {
            Assert.Contains(guide, covered);
        }
    }
}
