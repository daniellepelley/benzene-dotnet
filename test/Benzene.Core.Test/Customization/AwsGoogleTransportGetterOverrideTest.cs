using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Aws.Lambda.DynamoDb;
using Benzene.Aws.Lambda.EventBridge;
using Benzene.Aws.Lambda.Kafka;
using Benzene.Aws.Lambda.S3;
using Benzene.GoogleCloud.Functions.PubSub;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Customization;

// Regression tests for #160 (round-11 review): S3, DynamoDb, EventBridge, and Google Pub/Sub's
// DependencyInjectionExtensions used plain AddScoped for their per-context topic/body/header/version
// getters, so a user registration made earlier (ConfigureServices runs before Configure, where
// UseXxx registers the transport's defaults) was silently shadowed - last registration wins under MS
// DI. Converting to TryAddScoped/TryAddHeaderMessageVersionGetter (matching Benzene.Aws.Lambda.Sns,
// the already-correct reference) makes the earlier, more specific registration win instead.
// #229 (round 14-15): Benzene.Aws.Lambda.Kafka was a missed instance of this exact defect class -
// AddKafka() used plain AddScoped/AddHeaderMessageVersionGetter and was never included in the
// original #160 fix or this regression suite. See work/bug-fix-rulings-round14-15-2026-08.md WP-D.
public class AwsGoogleTransportGetterOverrideTest
{
    private class MarkerS3HeadersGetter : IMessageHeadersGetter<S3RecordContext>
    {
        public IDictionary<string, string> GetHeaders(S3RecordContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerKafkaHeadersGetter : IMessageHeadersGetter<KafkaContext>
    {
        public IDictionary<string, string> GetHeaders(KafkaContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerDynamoDbHeadersGetter : IMessageHeadersGetter<DynamoDbRecordContext>
    {
        public IDictionary<string, string> GetHeaders(DynamoDbRecordContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerEventBridgeHeadersGetter : IMessageHeadersGetter<EventBridgeContext>
    {
        public IDictionary<string, string> GetHeaders(EventBridgeContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    private class MarkerPubSubHeadersGetter : IMessageHeadersGetter<PubSubContext>
    {
        public IDictionary<string, string> GetHeaders(PubSubContext context) => new Dictionary<string, string> { ["x-marker"] = "on" };
    }

    [Fact]
    public void AddS3_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<S3RecordContext>, MarkerS3HeadersGetter>());
        services.UsingBenzene(x => x.AddS3());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<S3RecordContext>>();

        Assert.IsType<MarkerS3HeadersGetter>(getter);
    }

    [Fact]
    public void AddDynamoDb_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<DynamoDbRecordContext>, MarkerDynamoDbHeadersGetter>());
        services.UsingBenzene(x => x.AddDynamoDb());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<DynamoDbRecordContext>>();

        Assert.IsType<MarkerDynamoDbHeadersGetter>(getter);
    }

    [Fact]
    public void AddEventBridge_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<EventBridgeContext>, MarkerEventBridgeHeadersGetter>());
        services.UsingBenzene(x => x.AddEventBridge());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<EventBridgeContext>>();

        Assert.IsType<MarkerEventBridgeHeadersGetter>(getter);
    }

    [Fact]
    public void AddGooglePubSub_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<PubSubContext>, MarkerPubSubHeadersGetter>());
        services.UsingBenzene(x => x.AddGooglePubSub());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<PubSubContext>>();

        Assert.IsType<MarkerPubSubHeadersGetter>(getter);
    }

    [Fact]
    public void AddKafka_CustomHeadersGetterRegisteredFirst_Wins()
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<KafkaContext>, MarkerKafkaHeadersGetter>());
        services.UsingBenzene(x => x.AddKafka());

        var getter = services.BuildServiceProvider().GetRequiredService<IMessageHeadersGetter<KafkaContext>>();

        Assert.IsType<MarkerKafkaHeadersGetter>(getter);
    }
}
