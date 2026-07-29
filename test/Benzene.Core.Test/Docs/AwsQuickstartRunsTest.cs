using System;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core.TestHelpers;
using LambdaTestHostExtensions = Benzene.Aws.Lambda.Core.TestHelpers.BenzeneTestHostExtensions;
using Benzene.Testing;
using Xunit;

namespace Benzene.Test.Docs;

/// <summary>
/// Builds the AWS quickstart out of the markdown and sends a real request through it.
/// </summary>
/// <remarks>
/// <para>
/// This is the check that would have caught the bug. <c>docs/getting-started-aws.md</c>, followed
/// verbatim, compiled cleanly and then threw <c>Unable to resolve type MessageRouter…</c> on the very
/// first request, because <c>.AddBenzene()</c> was missing — and the registration hint told the
/// reader to add <c>.AddMessageHandlers(...)</c>, which they had just typed. A newcomer's first
/// contact with Benzene was a guide that did not work and an error that pointed the wrong way.
/// </para>
/// <para>
/// Compiling the snippet cannot see that. Running it can, so this one does: the StartUp class is the
/// one printed in the guide, loaded from the guide, and driven through the same test host the guide
/// itself recommends.
/// </para>
/// </remarks>
public class AwsQuickstartRunsTest
{
    [Fact]
    public async Task TheAwsQuickstartAnswersTheRequestItPromises()
    {
        var snippet = DocSnippetCompiler.Discover()
            .Single(x => x.DocPath == "docs/getting-started-aws.md" && x.Unit == "quickstart");

        var startUp = DocSnippetCompiler.Emit(snippet).GetType("StartUp");
        Assert.NotNull(startUp);

        using var host = BuildAwsLambdaTestHost(startUp);

        var response = await host.SendApiGatewayAsync(HttpBuilder.Create("GET", "/hello/world"));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("Hello world!", response.Body);
    }

    // BenzeneTestHost.Create<TStartUp>() is generic over a type that only exists at runtime here, so
    // the guide's own one-liner has to be reached reflectively. The call chain is otherwise exactly
    // the one docs/getting-started-aws.md step 6 prints.
    private static AwsLambdaBenzeneTestHost BuildAwsLambdaTestHost(Type startUp)
    {
        var builder = typeof(BenzeneTestHost)
            .GetMethod(nameof(BenzeneTestHost.Create))!
            .MakeGenericMethod(startUp)
            .Invoke(null, null);

        return (AwsLambdaBenzeneTestHost)typeof(LambdaTestHostExtensions)
            .GetMethod(nameof(LambdaTestHostExtensions.BuildAwsLambdaTestHost))!
            .MakeGenericMethod(startUp)
            .Invoke(null, new[] { builder })!;
    }
}
