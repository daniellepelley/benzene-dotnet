using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Clients;
using Benzene.Kafka.Core.Kafka;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients.Kafka;

/// <summary>
/// Covers the <c>OutboundContext</c> overloads of <c>UseKafka</c> - the ones that let an outbound route
/// (<c>AddOutboundRouting(...).Route(...)</c>) terminate in a Kafka produce without a hand-written
/// terminal middleware.
/// </summary>
public class OutboundKafkaContextConverterTest
{
    private static Mock<IProducer<string, string>> PersistingProducer()
    {
        var producer = new Mock<IProducer<string, string>>();
        producer
            .Setup(x => x.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryResult<string, string> { Status = PersistenceStatus.Persisted });
        return producer;
    }

    private static IBenzeneMessageSender SenderFor(IProducer<string, string> producer, Action<OutboundRoutingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(producer);
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(configure);
        return new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();
    }

    [Fact]
    public async Task SendAsync_RoutedThroughUseKafka_ProducesAndReturnsAcceptedVoidResult()
    {
        var producer = PersistingProducer();
        var sender = SenderFor(producer.Object, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseKafka()));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        producer.Verify(x => x.ProduceAsync(
            Defaults.Topic,
            It.Is<Message<string, string>>(message =>
                JsonConvert.DeserializeObject<ExampleRequestPayload>(message.Value).Name == "foo"),
            It.IsAny<CancellationToken>()));

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendAsync_ForwardsHeadersOntoTheRecord()
    {
        Message<string, string>? captured = null;
        var producer = new Mock<IProducer<string, string>>();
        producer
            .Setup(x => x.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, Message<string, string> message, CancellationToken _) => captured = message)
            .ReturnsAsync(new DeliveryResult<string, string> { Status = PersistenceStatus.Persisted });

        var sender = SenderFor(producer.Object, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseKafka()));

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" },
            new Dictionary<string, string> { { "tenantId", "tenant-1" } });

        Assert.NotNull(captured);
        Assert.Equal("tenant-1", Encoding.UTF8.GetString(captured!.Headers.GetLastBytes("tenantId")));
    }

    [Fact]
    public async Task SendAsync_WithAKeyHeader_UsesThatHeaderValueAsTheMessageKey()
    {
        Message<string, string>? captured = null;
        var producer = new Mock<IProducer<string, string>>();
        producer
            .Setup(x => x.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, Message<string, string> message, CancellationToken _) => captured = message)
            .ReturnsAsync(new DeliveryResult<string, string> { Status = PersistenceStatus.Persisted });

        var sender = SenderFor(producer.Object, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseKafka("tenantId")));

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" },
            new Dictionary<string, string> { { "tenantId", "tenant-1" } });

        Assert.NotNull(captured);
        Assert.Equal("tenant-1", captured!.Key);
    }

    [Fact]
    public async Task SendAsync_NotPersisted_ReturnsServiceUnavailable()
    {
        var producer = new Mock<IProducer<string, string>>();
        producer
            .Setup(x => x.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryResult<string, string> { Status = PersistenceStatus.NotPersisted });

        var sender = SenderFor(producer.Object, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseKafka()));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendAsync_ThroughTheExplicitInnerPipelineOverload_ProducesViaTheGivenProducer()
    {
        // The rung below: the caller configures the inner produce pipeline and hands over the producer
        // explicitly rather than having it resolved from the container.
        var producer = PersistingProducer();
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseKafka(builder => builder.UseKafkaClient(producer.Object))));
        var sender = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        producer.Verify(x => x.ProduceAsync(Defaults.Topic, It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()));
    }
}
