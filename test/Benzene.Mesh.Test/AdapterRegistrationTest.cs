using System.Linq;
using Amazon.CloudWatch;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Fleet.Tempo;
using Benzene.Mesh.Usage.ApplicationInsights;
using Benzene.Mesh.Usage.CloudWatch;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// The shared-infrastructure registrations inside the mesh adapter <c>Add*</c> extensions (an
/// <see cref="System.Net.Http.HttpClient"/>, an AWS/Azure client) are documented as "unless one is
/// already registered", but were wired with plain <c>AddSingleton</c>, which always registers and
/// silently overwrites a caller's own instance. This guards the fix (<c>TryAddSingleton</c>) and its
/// flip side: a genuinely additive registration (<see cref="IMeshUsageSource"/>) must NOT be de-duped
/// the same way, or a second adapter's source is silently dropped.
/// </summary>
public class AdapterRegistrationTest
{
    [Fact]
    public void AddTempoFleetReadModel_CallerPreRegisteredHttpClient_InstanceSurvives()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var callersHttpClient = new System.Net.Http.HttpClient();
        container.AddSingleton(callersHttpClient);

        container.AddTempoFleetReadModel(new TempoTraceSourceOptions("http://tempo:3200"));

        using var resolver = container.CreateServiceResolverFactory().CreateScope();
        Assert.Same(callersHttpClient, resolver.GetService<System.Net.Http.HttpClient>());
    }

    [Fact]
    public void AddCloudWatchUsageThenAddApplicationInsightsUsage_BothUsageSourcesRegister()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        // A fake IAmazonCloudWatch, pre-registered so AddCloudWatchUsage's TryAddSingleton does not
        // try to build a real AmazonCloudWatchClient - which needs a region this test environment
        // does not have. Resolving IMeshUsageSource is enough to prove both sources compose; neither
        // source's client is actually called.
        container.AddSingleton(new Mock<IAmazonCloudWatch>().Object);

        container.AddCloudWatchUsage(new CloudWatchUsageOptions());
        container.AddApplicationInsightsUsage(new ApplicationInsightsUsageOptions("workspace-id"));

        using var resolver = container.CreateServiceResolverFactory().CreateScope();
        var usageSources = resolver.GetServices<IMeshUsageSource>().ToList();

        Assert.Equal(2, usageSources.Count);
        Assert.Contains(usageSources, s => s is CloudWatchUsageSource);
        Assert.Contains(usageSources, s => s is ApplicationInsightsUsageSource);
    }
}
