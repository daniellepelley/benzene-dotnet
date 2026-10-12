using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventHubs.Producer;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.Aws.EventBridge;
using Benzene.Clients.Aws.Sns;
using Benzene.Clients.Aws.Sqs;
using Benzene.Clients.Azure.EventGrid;
using Benzene.Clients.Azure.EventHub;
using Benzene.Clients.Azure.QueueStorage;
using Benzene.Clients.Azure.ServiceBus;
using Benzene.Clients.GoogleCloud.PubSub;
using Benzene.Clients.Http;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Kafka.Core.Kafka;
using Benzene.Microsoft.Dependencies;
using Benzene.RabbitMq.RabbitMqSendMessage;
using Benzene.Resilience;
using Benzene.Results;
using Benzene.Test.Examples;
using Confluent.Kafka;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RabbitMQ.Client;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients;

/// <summary>
/// transport-bindings.md §1 (spec 1.1): a send that fails in transport - no connection, a refused or reset
/// request, the client's own deadline - before any reply arrives or, for a one-way client, before the broker
/// accepts the message, yields a <c>timeout</c>/<c>service-unavailable</c> result rather than an exception.
/// Every outbound route transport is driven through <see cref="IBenzeneMessageSender"/> with its SDK throwing.
/// </summary>
public class TransportFailureTest
{
    private const string Refused = "Connection refused (broker.example:443)";

    private static IBenzeneMessageSender SenderFor(Action<OutboundRoutingBuilder> configure, Action<ServiceCollection>? register = null)
    {
        var services = new ServiceCollection();
        register?.Invoke(services);
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(configure);
        return new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();
    }

    private static Task<IBenzeneResult<Void>> SendAsync(IBenzeneMessageSender sender) =>
        sender.SendAsync<ExampleRequestPayload, Void>(Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

    private static void AssertServiceUnavailable(IBenzeneResult result)
    {
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.False(result.IsSuccessful);
        Assert.Equal(Refused, Assert.Single(result.Errors).Message);
    }

    // --- classification ---

    [Fact]
    public void StatusOf_ATimeoutAnywhereInTheChain_IsTimeout()
    {
        Assert.Equal(BenzeneResultStatus.Timeout, TransportFailure.StatusOf(new TimeoutException()));
        // HttpClient.Timeout surfaces as a TaskCanceledException wrapping a TimeoutException.
        Assert.Equal(BenzeneResultStatus.Timeout, TransportFailure.StatusOf(new TaskCanceledException("t", new TimeoutException())));
        Assert.Equal(BenzeneResultStatus.Timeout, TransportFailure.StatusOf(new AggregateException(new InvalidOperationException(), new TimeoutException())));
    }

    [Fact]
    public void StatusOf_EverythingElse_IsServiceUnavailable()
    {
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, TransportFailure.StatusOf(new HttpRequestException(Refused)));
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, TransportFailure.StatusOf(new OperationCanceledException()));
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, TransportFailure.StatusOf(new System.IO.IOException("reset")));
    }

    [Fact]
    public void TransportFailure_IsAnUnsuccessfulResult_AndConvertsToAnyResponseType()
    {
        var failure = TransportFailure.From(new HttpRequestException(Refused));

        Assert.False(failure.IsSuccessful);
        Assert.Null(failure.PayloadAsObject);
        var typed = failure.ToResult<ExampleResponsePayload>();
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, typed.Status);
        Assert.Equal(Refused, Assert.Single(typed.Errors).Message);
    }

    // --- the converter middleware: only the send is covered ---

    private sealed class FailingConverter(bool mapsFailures, bool failCreate = false) : IContextConverter<OutboundContext, object>, ITransportFailureMapper<OutboundContext>
    {
        public bool Mapped { get; private set; }

        public Task<object> CreateRequestAsync(OutboundContext contextIn) =>
            failCreate ? throw new FormatException("cannot serialize") : Task.FromResult(new object());

        public Task MapResponseAsync(OutboundContext contextIn, object contextOut) => Task.CompletedTask;

        public void MapTransportFailure(OutboundContext contextIn, Exception exception)
        {
            if (!mapsFailures)
            {
                throw new InvalidOperationException("not expected");
            }

            Mapped = true;
            contextIn.Response = TransportFailure.From(exception);
        }
    }

    private static IMiddlewarePipeline<object> Throwing(Exception exception) =>
        new MiddlewarePipelineBuilder<object>(new NullBenzeneServiceContainer())
            .Use(_ => new FuncWrapperMiddleware<object>("throw", (_, _) => throw exception))
            .Build();

    [Fact]
    public async Task ContextConverterMiddleware_TheSendThrows_TheFailureMapperRecordsAResult()
    {
        var converter = new FailingConverter(mapsFailures: true);
        var middleware = new ContextConverterMiddleware<OutboundContext, object>(converter, Throwing(new HttpRequestException(Refused)), NullServiceResolver());
        var context = new OutboundContext("t", new object());

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.True(converter.Mapped);
        AssertServiceUnavailable(Assert.IsType<TransportFailure>(context.Response));
    }

    [Fact]
    public async Task ContextConverterMiddleware_BuildingTheRequestFails_ItStillThrows()
    {
        // Serialization is not a transport failure.
        var converter = new FailingConverter(mapsFailures: true, failCreate: true);
        var middleware = new ContextConverterMiddleware<OutboundContext, object>(converter, Throwing(new HttpRequestException(Refused)), NullServiceResolver());

        await Assert.ThrowsAsync<FormatException>(() => middleware.HandleAsync(new OutboundContext("t", new object()), () => Task.CompletedTask));
    }

    [Fact]
    public async Task ContextConverterMiddleware_TheCallersOwnCancellation_StillPropagates()
    {
        // The ambient token's cancellation belongs to whoever owns it (an upstream cancel, a UseTimeout()).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var resolver = new Mock<IServiceResolver>();
        resolver.Setup(x => x.TryGetService<ICancellationTokenAccessor>()).Returns(accessor);
        var converter = new FailingConverter(mapsFailures: false);
        var middleware = new ContextConverterMiddleware<OutboundContext, object>(converter, Throwing(new OperationCanceledException(cts.Token)), resolver.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.HandleAsync(new OutboundContext("t", new object()), () => Task.CompletedTask));
        Assert.False(converter.Mapped);
    }

    private static IServiceResolver NullServiceResolver() => new Mock<IServiceResolver>().Object;

    // --- every outbound route transport ---

    [Fact]
    public async Task Sqs_SendFails_IsAServiceUnavailableResult()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseSqs(Defaults.SqsQueueUrl, healthCheck: false)),
            services => services.AddSingleton(sqs.Object)));

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task Sqs_TheSdksOwnTimeout_IsATimeoutResult()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request timed out", new TimeoutException()));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseSqs(Defaults.SqsQueueUrl, healthCheck: false)),
            services => services.AddSingleton(sqs.Object)));

        Assert.Equal(BenzeneResultStatus.Timeout, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task ARoutesOwnDeadline_UseTimeout_IsATimeoutResult()
    {
        // The client's own deadline: UseTimeout() on the route fires while the SDK call is in flight.
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Returns<SendMessageRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK };
            });

        var result = await SendAsync(SenderFor(
            routing => routing.Route(Defaults.Topic, p => p.UseTimeout(TimeSpan.FromMilliseconds(50)).UseSqs(Defaults.SqsQueueUrl, healthCheck: false)),
            services =>
            {
                services.AddSingleton(sqs.Object);
                services.AddScoped<CancellationTokenAccessor>();
                services.AddScoped<ICancellationTokenAccessor>(sp => sp.GetRequiredService<CancellationTokenAccessor>());
            }));

        Assert.Equal(BenzeneResultStatus.Timeout, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task Sns_PublishFails_IsAServiceUnavailableResult()
    {
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(x => x.PublishAsync(It.IsAny<Amazon.SimpleNotificationService.Model.PublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseSns("arn:aws:sns:eu-west-1:1:t", healthCheck: false)),
            services => services.AddSingleton(sns.Object)));

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task EventBridge_PutEventsFails_IsAServiceUnavailableResult()
    {
        var eventBridge = new Mock<IAmazonEventBridge>();
        eventBridge.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseEventBridge("orders", healthCheck: false)),
            services => services.AddSingleton(eventBridge.Object)));

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task ServiceBus_SendFails_IsAServiceUnavailableResult()
    {
        var sender = new Mock<ServiceBusSender>();
        sender.Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException(Refused, ServiceBusFailureReason.ServiceCommunicationProblem));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseServiceBus(sender.Object))));

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task EventHub_SendFails_IsAServiceUnavailableResult()
    {
        var producer = new Mock<EventHubProducerClient>();
        producer.Setup(x => x.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseEventHub(producer.Object, healthCheck: false))));

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task EventGrid_SendFails_IsAServiceUnavailableResult()
    {
        var publisher = new Mock<EventGridPublisherClient>();
        publisher.Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseEventGrid("orders", publisher.Object))));

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task QueueStorage_SendFails_IsAServiceUnavailableResult()
    {
        var queue = new Mock<QueueClient>();
        queue.Setup(x => x.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseQueueStorage(queue.Object, healthCheck: false))));

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task PubSub_PublishFails_IsAServiceUnavailableResult()
    {
        var publisher = new Mock<PublisherServiceApiClient>();
        publisher.Setup(x => x.PublishAsync(It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()))
            .ThrowsAsync(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic,
            p => p.UsePubSub("projects/p/topics/t", action => action.UsePubSubClient(publisher.Object)))));

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task Kafka_ProduceFails_IsAServiceUnavailableResult()
    {
        var producer = new Mock<IProducer<string, string>>();
        producer.Setup(x => x.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KafkaException(new Error(ErrorCode.Local_Transport, Refused)));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseKafka()),
            services => services.AddSingleton(producer.Object)));

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task RabbitMq_PublishFails_IsAServiceUnavailableResult()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException(Refused));

        var result = await SendAsync(SenderFor(routing => routing.Route(Defaults.Topic, p => p.UseRabbitMq(channel.Object))));

        AssertServiceUnavailable(result);
    }

    // --- the reply-receiving HTTP transports ---

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    [Fact]
    public async Task BenzeneMessageOverHttp_NoConnection_IsAServiceUnavailableResult_ForATypedCaller()
    {
        var http = new HttpClient(new ThrowingHandler(new HttpRequestException(Refused)));
        var sender = SenderFor(routing => routing.Route(Defaults.Topic,
            p => p.UseBenzeneMessageOverHttp("https://orders.example/benzene/invoke", b => b.UseHttpClient(http))));

        var result = await sender.SendAsync<ExampleRequestPayload, ExampleResponsePayload>(Defaults.Topic, new ExampleRequestPayload { Id = 1 });

        AssertServiceUnavailable(result);
    }

    [Fact]
    public async Task BenzeneMessageOverHttp_HttpClientTimeout_IsATimeoutResult()
    {
        var http = new HttpClient(new ThrowingHandler(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException())));
        var sender = SenderFor(routing => routing.Route(Defaults.Topic,
            p => p.UseBenzeneMessageOverHttp("https://orders.example/benzene/invoke", b => b.UseHttpClient(http))));

        var result = await sender.SendAsync<ExampleRequestPayload, ExampleResponsePayload>(Defaults.Topic, new ExampleRequestPayload { Id = 1 });

        Assert.Equal(BenzeneResultStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task HttpBenzeneMessageClient_HttpClientTimeout_IsATimeoutResult()
    {
        var client = new HttpBenzeneMessageClient(
            new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out", new TimeoutException()))), "https://orders.example/benzene/invoke");

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task PlainHttp_UseHttp_NoConnection_IsAServiceUnavailableResult()
    {
        var http = new HttpClient(new ThrowingHandler(new HttpRequestException(Refused)));
        var pipeline = new MiddlewarePipelineBuilder<Benzene.Abstractions.Messages.BenzeneClient.IBenzeneClientContext<string, string>>(new NullBenzeneServiceContainer())
            .UseHttp<string, string>("GET", "https://orders.example/orders/1", b => b.UseHttpClient(http))
            .Build();
        var context = new Benzene.Abstractions.Messages.BenzeneClient.BenzeneClientContext<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        await pipeline.HandleAsync(context, NullServiceResolver());

        AssertServiceUnavailable(context.Response);
    }
}
