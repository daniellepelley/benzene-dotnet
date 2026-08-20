using System.Text.RegularExpressions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Schema.OpenApi.EventService;
using Benzene.Test.Autogen.CodeGen.Model;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

/// <summary>
/// Regression test for the projection-comparability bug fixed alongside the migration to the
/// spec-pinned contractHash (contract-document.md §6; work/archive/cross-language-clients-plan-2026-08.md Phase 2 step
/// 3): before this fix, a default-generated client's embedded hash (already domain-scoped by
/// <see cref="TopicScope"/> before <see cref="MessageClientSdkBuilder"/> ever hashes it) could never
/// equal <see cref="Benzene.HealthChecks.Schema.SchemaHealthCheck"/>'s provider-side hash (computed
/// over every registered handler, reserved topics included) for the very same service - the two
/// were, silently, always-mismatching different projections. Both now hash the same domain
/// projection via <see cref="ContractHash"/>, so they agree.
/// </summary>
public class ContractHashProviderClientAlignmentTest
{
    [Fact]
    public void DefaultGeneratedClientHash_MatchesProviderPublishedHash_ForTheSameService()
    {
        var handlers = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("user:get", typeof(GetUserMessage), typeof(UserDto)),
            MessageHandlerDefinition.CreateInstance("user:create", typeof(CreateUserMessage), typeof(UserDto)),
            // Every conformant service registers reserved handlers too (benzene:spec,
            // benzene:healthcheck, ...). SchemaHealthCheck.ExecuteAsync hashes every registered
            // handler (IMessageHandlerDefinitionLookUp.GetAllHandlers - it does not, and must not,
            // pre-filter); a default client generation never sees this topic at all (TopicScope
            // excludes it before MessageClientSdkBuilder gets the document). The two hashes must
            // still agree, because ContractHash applies the same domain-projection rule on both
            // sides (contract-document.md §5.1/§6.2).
            MessageHandlerDefinition.CreateInstance("benzene:healthcheck", typeof(GetUserMessage), typeof(UserDto)),
        };

        // Client side: exactly what a consumer's generated {Service}ServiceClient.HashCode carries.
        var options = new ClientSdkOptions { ServiceName = "User", Namespace = "Benzene.Service.Clients.User" };
        var result = new MessageClientSdkBuilder(options)
            .BuildCodeFiles(handlers.ToEventServiceDocument())
            .ToFilesDictionary();
        var clientHash = ExtractHashCode(result["UserServiceClient.cs"]);

        // Provider side: exactly what SchemaHealthCheck.ExecuteAsync publishes.
        var providerHash = ContractHash.Compute(handlers);

        Assert.StartsWith("sha256:", clientHash);
        Assert.Equal(providerHash, clientHash);
    }

    private static string ExtractHashCode(string classSource)
    {
        var match = Regex.Match(classSource, "HashCode => \"(?<hash>[^\"]+)\"");
        Assert.True(match.Success, "Generated client source did not contain a HashCode property.");
        return match.Groups["hash"].Value;
    }
}
