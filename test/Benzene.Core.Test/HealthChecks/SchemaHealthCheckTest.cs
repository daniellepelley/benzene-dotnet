using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Clients.HealthChecks;
using Benzene.CodeGen.Core;
using Benzene.Core.MessageHandlers;
using Benzene.HealthChecks.Core;
using Benzene.HealthChecks.Schema;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Moq;
using Xunit;
using System.Threading;

namespace Benzene.Test.HealthChecks;

public class SchemaHealthCheckTest
{
    // Simple request/response POCOs so the served document has real schemas to hash.
    public class CreateThing { public string Name { get; set; } = ""; }
    public class ThingCreated { public string Id { get; set; } = ""; }

    private static IMessageHandlerDefinition[] Handlers() =>
    [
        MessageHandlerDefinition.CreateInstance("thing:create", typeof(CreateThing), typeof(ThingCreated)),
    ];

    // The document benzene:spec serves for these handlers.
    private static string Served(IMessageHandlerDefinition[] handlers) =>
        handlers.ToEventServiceDocument().SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);

    [Fact]
    public async Task ExecuteAsync_PublishesTheServedContractHash()
    {
        var served = Served(Handlers());

        var result = await new SchemaHealthCheck(() => served).ExecuteAsync(CancellationToken.None);

        Assert.Equal(SchemaHealthCheckConstants.Type, result.Type);
        // The provider publishes the contractHash its served document carries (contract-document.md
        // §6) - the value a default generated client embeds - never a private hash of its own.
        Assert.Equal(System.Text.Json.JsonDocument.Parse(served).RootElement.GetProperty("contractHash").GetString(),
            result.Data[SchemaHealthCheckConstants.HashCodeKey]?.ToString());
        Assert.Equal(ContractHash.Compute(served), result.Data[SchemaHealthCheckConstants.HashCodeKey]?.ToString());
    }

    [Fact]
    public async Task EndToEnd_ProviderHashMatchesClientBakedHash_ReportsMatch()
    {
        var handlers = Handlers();
        // A consumer's generated client bakes in the served document's contractHash at generation time.
        var clientBakedHash = ContractHash.Compute(Served(handlers));

        var schemaResult = (HealthCheckResult)await new SchemaHealthCheck(() => Served(handlers)).ExecuteAsync(CancellationToken.None);
        var providerResponse = new HealthCheckResponse(true,
            new Dictionary<string, HealthCheckResult> { [SchemaHealthCheckConstants.Type] = schemaResult });

        var processed = ClientHealthCheckProcessor.Process(providerResponse, clientBakedHash);

        var match = (ClientHashMatch)processed.HealthChecks[SchemaHealthCheckConstants.Type]
            .Data[SchemaHealthCheckConstants.MatchKey];
        Assert.True(match.IsMatch);
    }

    [Fact]
    public async Task EndToEnd_ProviderContractChanged_ReportsDrift()
    {
        // The client was generated against the original contract...
        var clientBakedHash = ContractHash.Compute(Served(Handlers()));

        // ...but the provider now exposes a different contract (an extra handler).
        var changedHandlers = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("thing:create", typeof(CreateThing), typeof(ThingCreated)),
            MessageHandlerDefinition.CreateInstance("thing:delete", typeof(CreateThing), typeof(ThingCreated)),
        };
        var schemaResult = (HealthCheckResult)await new SchemaHealthCheck(() => Served(changedHandlers)).ExecuteAsync(CancellationToken.None);
        var providerResponse = new HealthCheckResponse(true,
            new Dictionary<string, HealthCheckResult> { [SchemaHealthCheckConstants.Type] = schemaResult });

        var processed = ClientHealthCheckProcessor.Process(providerResponse, clientBakedHash);

        var match = (ClientHashMatch)processed.HealthChecks[SchemaHealthCheckConstants.Type]
            .Data[SchemaHealthCheckConstants.MatchKey];
        Assert.False(match.IsMatch);
    }
}
