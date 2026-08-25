using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.HealthChecks;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using System.Threading;

namespace Benzene.Test.Clients;

/// <summary>
/// The library-provided downstream health call: <see cref="ServiceHealthCheckClient"/> sends
/// <c>benzene:healthcheck</c> over <see cref="IBenzeneMessageSender"/>, so calling a downstream's
/// health check needs no generated code at all - the payload is standard and known up front.
/// </summary>
public class ServiceHealthCheckClientTest
{
    private const string ServiceHash = "provider-contract-v2";
    private const string ClientHash = "provider-contract-v1";

    // A provider's health response as it comes off the wire: un-annotated, carrying the schema check
    // whose Data publishes the provider's live contract hash.
    private static HealthCheckResponse ProviderResponse(string serviceHash)
    {
        var data = new Dictionary<string, object> { [SchemaHealthCheckConstants.HashCodeKey] = serviceHash };
        var schema = (HealthCheckResult)HealthCheckResult.CreateInstance(true, SchemaHealthCheckConstants.Type, data);
        return new HealthCheckResponse(true,
            new Dictionary<string, HealthCheckResult> { [SchemaHealthCheckConstants.Type] = schema });
    }

    private static Mock<IBenzeneMessageSender> SenderReturning(IBenzeneResult<HealthCheckResponse> result)
    {
        var sender = new Mock<IBenzeneMessageSender>();
        sender.Setup(x => x.SendAsync<Benzene.Abstractions.Results.Void, HealthCheckResponse>(
                It.IsAny<string>(), It.IsAny<Benzene.Abstractions.Results.Void>(), It.IsAny<IDictionary<string, string>>()))
            .ReturnsAsync(result);
        return sender;
    }

    private static ClientHashMatch? MatchIn(HealthCheckResponse response) =>
        response.HealthChecks[SchemaHealthCheckConstants.Type].Data
            .TryGetValue(SchemaHealthCheckConstants.MatchKey, out var raw)
            ? raw as ClientHashMatch
            : null;

    [Fact]
    public async Task SendsTheReservedHealthCheckTopic_WithAVoidRequest()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));

        await new ServiceHealthCheckClient(sender.Object, ClientHash).HealthCheckAsync();

        sender.Verify(x => x.SendAsync<Benzene.Abstractions.Results.Void, HealthCheckResponse>(
            BenzeneTopic.HealthCheck, It.IsAny<Benzene.Abstractions.Results.Void>(), It.IsAny<IDictionary<string, string>>()), Times.Once);
        Assert.Equal("benzene:healthcheck", BenzeneTopic.HealthCheck);
    }

    [Fact]
    public async Task WithExpectedHash_AnnotatesDrift_AsTheGeneratedHealthCheckUsedTo()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));

        var result = await new ServiceHealthCheckClient(sender.Object, ClientHash).HealthCheckAsync();

        var match = MatchIn(result.Payload);
        Assert.NotNull(match);
        Assert.False(match!.IsMatch);
        Assert.Equal(ServiceHash, match.ServiceHashCode);
        Assert.Equal(ClientHash, match.ClientHashCode);
    }

    [Fact]
    public async Task WithExpectedHash_MatchingContract_ReportsAMatch()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));

        var result = await new ServiceHealthCheckClient(sender.Object, ServiceHash).HealthCheckAsync();

        Assert.True(MatchIn(result.Payload)!.IsMatch);
    }

    [Fact]
    public async Task WithoutExpectedHash_ReachabilityOnly_ReportsNoDriftVerdict()
    {
        // The degrade-sensibly requirement: no expected hash means no opinion on drift. Running the
        // processor against "" would write IsMatch:false against a real provider hash, which
        // ClientHealthCheck reads as genuine drift.
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));

        var result = await new ServiceHealthCheckClient(sender.Object).HealthCheckAsync();

        Assert.Null(MatchIn(result.Payload));
        Assert.Equal(string.Empty, new ServiceHealthCheckClient(sender.Object).HashCode);
    }

    [Fact]
    public async Task WithoutExpectedHash_ReachableProvider_IsOkNotAFalseWarning()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));
        var check = new ClientHealthCheck("Payments", new ServiceHealthCheckClient(sender.Object));

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal(true, result.Data["reachable"]);
    }

    [Fact]
    public async Task WithExpectedHash_DriftSurfacesAsAWarning_ThroughClientHealthCheck()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));
        var check = new ClientHealthCheck("Payments", new ServiceHealthCheckClient(sender.Object, ClientHash));

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Warning, result.Status);
    }

    [Fact]
    public async Task NoPayload_PassesTheFailureStraightThrough_Unannotated()
    {
        var sender = SenderReturning(BenzeneResult.Set<HealthCheckResponse>("service-unavailable", false));

        var result = await new ServiceHealthCheckClient(sender.Object, ClientHash).HealthCheckAsync();

        Assert.Null(result.Payload);
        Assert.Equal("service-unavailable", result.Status);
    }

    [Fact]
    public async Task HashCode_ExposesTheExpectedHash_SoAGeneratedClientsHashCodeCanBePassedIn()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));

        Assert.Equal(ClientHash, new ServiceHealthCheckClient(sender.Object, ClientHash).HashCode);
        await Task.CompletedTask;
    }

    // --- registration ---------------------------------------------------------------------------

    private sealed class TestRegister : IRegisterDependency
    {
        private readonly IServiceCollection _services;
        public TestRegister(IServiceCollection services) => _services = services;
        public void Register(Action<IBenzeneServiceContainer> action) => action(new MicrosoftBenzeneServiceContainer(_services));
    }

    private static async Task<IHealthCheckResult> RunSingle(IBenzeneMessageSender sender, Action<IHealthCheckBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => sender);
        var builder = new HealthCheckBuilder(new TestRegister(services));
        configure(builder);

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var scope = factory.CreateScope();
        var check = Assert.Single(builder.GetHealthChecks(scope));
        return await check.ExecuteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AddServiceCheck_ResolvesTheSenderFromTheContainer_AndReportsDrift()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));

        var result = await RunSingle(sender.Object, b => b.AddServiceCheck("Payments", ClientHash));

        Assert.Equal("Payments", result.Type);
        Assert.Equal(HealthCheckStatus.Warning, result.Status);
        Assert.Contains(result.Dependencies, d => d.Kind == "Service" && d.Name == "Payments");
    }

    [Fact]
    public async Task AddServiceCheck_WithoutAHash_IsReachabilityOnly()
    {
        var sender = SenderReturning(BenzeneResult.Ok(ProviderResponse(ServiceHash)));

        var result = await RunSingle(sender.Object, b => b.AddServiceCheck("Payments"));

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.False(result.Data.ContainsKey(SchemaHealthCheckConstants.MatchKey));
    }

    [Fact]
    public async Task AddServiceCheck_UnreachableProvider_IsFailedNotThrown()
    {
        var sender = new Mock<IBenzeneMessageSender>();
        sender.Setup(x => x.SendAsync<Benzene.Abstractions.Results.Void, HealthCheckResponse>(
                It.IsAny<string>(), It.IsAny<Benzene.Abstractions.Results.Void>(), It.IsAny<IDictionary<string, string>>()))
            .ThrowsAsync(new UnroutedTopicException(BenzeneTopic.HealthCheck));

        var result = await RunSingle(sender.Object, b => b.AddServiceCheck("Payments", ClientHash));

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal(false, result.Data["reachable"]);
    }
}
