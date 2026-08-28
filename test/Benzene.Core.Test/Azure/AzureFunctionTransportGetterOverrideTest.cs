using System.Collections.Generic;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Azure.Function.AspNet;
using Benzene.Azure.Function.EventGrid;
using Benzene.Azure.Function.EventHub.Function;
using Benzene.Azure.Function.Kafka;
using Benzene.Azure.Function.QueueStorage;
using Benzene.Azure.Function.ServiceBus;
using Benzene.Azure.Function.Timer;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Azure;

// Regression tests for #234 (round 14-15 review): all seven Azure Functions packages'
// DependencyInjectionExtensions used plain AddScoped/AddHeaderMessageVersionGetter for their
// per-context topic/body/header/version/result-setter getters, so a user registration made earlier
// (ConfigureServices runs before Configure, where UseXxx registers the transport's defaults) was
// silently shadowed - last registration wins under MS DI. This is the identical defect class round
// 11's #160 fixed for the AWS/Google transports (see AwsGoogleTransportGetterOverrideTest, which this
// suite mirrors) and a prior pass fixed for the self-hosted Azure workers
// (Benzene.Azure.ServiceBus/.EventHub), but it was never carried to the Functions-triggered Azure
// family until now. Converting to TryAddScoped/TryAddHeaderMessageVersionGetter (matching the
// already-correct Benzene.Aws.Lambda.Sns/Sqs reference pattern) makes the earlier, more specific
// registration win instead. One test class for the whole family, not seven scattered one-offs - see
// work/bug-fix-rulings-round14-15-2026-08.md WP-D.
public class AzureFunctionTransportGetterOverrideTest
{
    private class MarkerQueueStorageHeadersGetter : IMessageHeadersGetter<QueueStorageContext>
    {
        public IDictionary<string, string> GetHeaders(QueueStorageContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerEventGridHeadersGetter : IMessageHeadersGetter<EventGridContext>
    {
        public IDictionary<string, string> GetHeaders(EventGridContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerEventHubHeadersGetter : IMessageHeadersGetter<EventHubContext>
    {
        public IDictionary<string, string> GetHeaders(EventHubContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerKafkaHeadersGetter : IMessageHeadersGetter<KafkaContext>
    {
        public IDictionary<string, string> GetHeaders(KafkaContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerServiceBusHeadersGetter : IMessageHeadersGetter<ServiceBusContext>
    {
        public IDictionary<string, string> GetHeaders(ServiceBusContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerTimerHeadersGetter : IMessageHeadersGetter<TimerContext>
    {
        public IDictionary<string, string> GetHeaders(TimerContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerAspNetHeadersGetter : IMessageHeadersGetter<AspNetContext>
    {
        public IDictionary<string, string> GetHeaders(AspNetContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerAspNetVersionGetter : IMessageVersionGetter<AspNetContext>
    {
        public string GetVersion(AspNetContext context) => "marker-version";
    }

    [Fact]
    public void AddAzureQueueStorage_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<QueueStorageContext>, MarkerQueueStorageHeadersGetter>());
        services.UsingBenzene(x => x.AddAzureQueueStorage());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<QueueStorageContext>>();

        Assert.IsType<MarkerQueueStorageHeadersGetter>(getter);
    }

    [Fact]
    public void AddAzureEventGrid_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<EventGridContext>, MarkerEventGridHeadersGetter>());
        services.UsingBenzene(x => x.AddAzureEventGrid());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<EventGridContext>>();

        Assert.IsType<MarkerEventGridHeadersGetter>(getter);
    }

    [Fact]
    public void AddAzureEventHub_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<EventHubContext>, MarkerEventHubHeadersGetter>());
        services.UsingBenzene(x => x.AddAzureEventHub());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<EventHubContext>>();

        Assert.IsType<MarkerEventHubHeadersGetter>(getter);
    }

    [Fact]
    public void AddAzureKafka_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<KafkaContext>, MarkerKafkaHeadersGetter>());
        services.UsingBenzene(x => x.AddAzureKafka());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<KafkaContext>>();

        Assert.IsType<MarkerKafkaHeadersGetter>(getter);
    }

    [Fact]
    public void AddAzureServiceBus_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<ServiceBusContext>, MarkerServiceBusHeadersGetter>());
        services.UsingBenzene(x => x.AddAzureServiceBus());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<ServiceBusContext>>();

        Assert.IsType<MarkerServiceBusHeadersGetter>(getter);
    }

    [Fact]
    public void AddAzureTimer_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<TimerContext>, MarkerTimerHeadersGetter>());
        services.UsingBenzene(x => x.AddAzureTimer());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<TimerContext>>();

        Assert.IsType<MarkerTimerHeadersGetter>(getter);
    }

    [Fact]
    public void AddAspNet_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<AspNetContext>, MarkerAspNetHeadersGetter>());
        services.UsingBenzene(x => x.AddAspNet());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<AspNetContext>>();

        Assert.IsType<MarkerAspNetHeadersGetter>(getter);
    }

    // AspNet is the one package in this family with its own IMessageVersionGetter<TContext>
    // registration (every other transport gets it via TryAddHeaderMessageVersionGetter<TContext>,
    // already covered generically) - see work/bug-fix-rulings-round14-15-2026-08.md WP-D.
    [Fact]
    public void AddAspNet_CustomVersionGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageVersionGetter<AspNetContext>, MarkerAspNetVersionGetter>());
        services.UsingBenzene(x => x.AddAspNet());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageVersionGetter<AspNetContext>>();

        Assert.IsType<MarkerAspNetVersionGetter>(getter);
    }
}
