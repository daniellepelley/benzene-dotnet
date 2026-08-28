using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Kafka.Core.Kafka;
using Benzene.Microsoft.Dependencies;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Kafka;

/// <summary>
/// #237: <see cref="KafkaClientMiddleware"/> resolves the ambient <see cref="ICancellationTokenAccessor"/>
/// and threads its token into <c>IProducer.ProduceAsync</c>, on both the constructor-supplied-producer
/// path (<c>UseKafkaClient(producer)</c>) and the DI-resolved path. Mirrors <c>PubSubCancellationTest</c>'s
/// "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c> for the token under
/// test, unlike the pre-existing mapper test that only ever verified the shape of the call.
/// </summary>
public class KafkaClientMiddlewareCancellationTest
{
    private static KafkaSendMessageContext SampleContext() =>
        new("my-topic", new Message<string, string> { Value = "hello" });

    [Fact]
    public async Task HandleAsync_ConstructorAccessor_ForwardsTheAmbientTokenToProduceAsync()
    {
        using var cts = new CancellationTokenSource();
        var producer = new Mock<IProducer<string, string>>();
        var context = SampleContext();
        producer
            .Setup(x => x.ProduceAsync("my-topic", context.Message, cts.Token))
            .ReturnsAsync(new DeliveryResult<string, string> { Topic = "my-topic", Status = PersistenceStatus.Persisted });

        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var middleware = new KafkaClientMiddleware(producer.Object, accessor);

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        producer.Verify(x => x.ProduceAsync("my-topic", context.Message, cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseKafkaClient_GivenProducer_ResolvesTheAccessorFromThePipeline_ForwardsTheAmbientTokenToProduceAsync()
    {
        using var cts = new CancellationTokenSource();
        var producer = new Mock<IProducer<string, string>>();
        var context = SampleContext();
        producer
            .Setup(x => x.ProduceAsync("my-topic", context.Message, cts.Token))
            .ReturnsAsync(new DeliveryResult<string, string> { Topic = "my-topic", Status = PersistenceStatus.Persisted });

        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();

        var pipeline = new MiddlewarePipelineBuilder<KafkaSendMessageContext>(new NullBenzeneServiceContainer())
            .UseKafkaClient(producer.Object)
            .Build();

        await pipeline.HandleAsync(context, resolver);

        producer.Verify(x => x.ProduceAsync("my-topic", context.Message, cts.Token), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NoAccessor_ProducesWithNoneToken()
    {
        var producer = new Mock<IProducer<string, string>>();
        var context = SampleContext();
        producer
            .Setup(x => x.ProduceAsync("my-topic", context.Message, CancellationToken.None))
            .ReturnsAsync(new DeliveryResult<string, string> { Topic = "my-topic", Status = PersistenceStatus.Persisted });

        var middleware = new KafkaClientMiddleware(producer.Object);

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        producer.Verify(x => x.ProduceAsync("my-topic", context.Message, CancellationToken.None), Times.Once);
    }
}
