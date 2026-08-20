using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Core;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.Sqs;

/// <summary>
/// Phase 2 of the cancellation initiative (<c>work/archive/cancellation-design-2026-08.md</c>): the SQS consumer's
/// run/shutdown token now reaches each message's per-message scope, so a handler that observes it can
/// abort in-flight work instead of finishing (and being deleted) after the host asked it to stop.
/// </summary>
public class SqsConsumerCancellationTest
{
    private static IServiceResolverFactory CreateFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped<CancellationTokenAccessor>();
        services.AddScoped<ICancellationTokenAccessor>(x => x.GetService<CancellationTokenAccessor>());
        return new MicrosoftServiceResolverFactory(services);
    }

    [Fact]
    public async Task HandleAsync_WithToken_SeedsEachMessagesScope()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<SqsConsumerMessageContext>>();
        CancellationToken observed = default;
        mockPipeline
            .Setup(x => x.HandleAsync(It.IsAny<SqsConsumerMessageContext>(), It.IsAny<IServiceResolver>()))
            .Returns((SqsConsumerMessageContext context, IServiceResolver resolver) =>
            {
                observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                context.MessageResult = Benzene.Results.BenzeneResult.Ok();
                return Task.CompletedTask;
            });

        var application = new SqsConsumerApplication(mockPipeline.Object);
        using var cts = new CancellationTokenSource();

        var response = new ReceiveMessageResponse
        {
            Messages = new List<Message> { new Message { MessageId = "m1", ReceiptHandle = "r1" } }
        };

        await application.HandleAsync(response, CreateFactory(), cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task HandleAsync_WithoutToken_LeavesEachMessagesScopeAtNone()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<SqsConsumerMessageContext>>();
        CancellationToken observed = CancellationToken.None;
        var seen = false;
        mockPipeline
            .Setup(x => x.HandleAsync(It.IsAny<SqsConsumerMessageContext>(), It.IsAny<IServiceResolver>()))
            .Returns((SqsConsumerMessageContext context, IServiceResolver resolver) =>
            {
                observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                seen = true;
                context.MessageResult = Benzene.Results.BenzeneResult.Ok();
                return Task.CompletedTask;
            });

        var application = new SqsConsumerApplication(mockPipeline.Object);
        var response = new ReceiveMessageResponse
        {
            Messages = new List<Message> { new Message { MessageId = "m1", ReceiptHandle = "r1" } }
        };

        await application.HandleAsync(response, CreateFactory());

        Assert.True(seen);
        Assert.Equal(CancellationToken.None, observed);
    }

    [Fact]
    public async Task HandleAsync_PerMessage_HandlerThrowsOceWithFiredToken_MessageReportedFailed_NotDeleted()
    {
        // This is the shipped exception contract (ExceptionHandlerMiddleware / MessageHandler): an
        // OperationCanceledException whose token has fired is not a business failure to swallow, it
        // propagates. SqsConsumerApplication's own per-message catch (Exception) then treats it like
        // any other throw under PerMessage: the message is reported failed, not successful - so
        // SqsConsumer (below) never deletes it and the queue redelivers.
        using var cts = new CancellationTokenSource();

        var mockPipeline = new Mock<IMiddlewarePipeline<SqsConsumerMessageContext>>();
        mockPipeline
            .Setup(x => x.HandleAsync(It.IsAny<SqsConsumerMessageContext>(), It.IsAny<IServiceResolver>()))
            .Returns((SqsConsumerMessageContext context, IServiceResolver resolver) =>
            {
                var token = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                Assert.Equal(cts.Token, token);
                // Simulate the host's shutdown token firing while this message is in flight.
                cts.Cancel();
                throw new OperationCanceledException(token);
            });

        var application = new SqsConsumerApplication(mockPipeline.Object, new SqsConsumerOptions { AckMode = SqsConsumerAckMode.PerMessage });

        var response = new ReceiveMessageResponse
        {
            Messages = new List<Message> { new Message { MessageId = "in-flight", ReceiptHandle = "r1" } }
        };

        var result = await application.HandleAsync(response, CreateFactory(), cts.Token);

        Assert.Empty(result.SuccessfulMessages);
        Assert.Equal("in-flight", Assert.Single(result.FailedMessages).MessageId);
    }

    [Fact]
    public async Task StartAsync_HandlerCancelsMidMessage_MessageIsNotDeletedFromTheQueue()
    {
        // End-to-end through SqsConsumer.StartAsync: the run token it receives now reaches the
        // handler's ambient accessor. A handler that observes the fired token and throws OCE leaves
        // the message off the delete batch, and the poll loop exits cleanly once the same token (also
        // the consumer's own run token) is observed cancelled.
        using var cts = new CancellationTokenSource();

        var message = new Message { MessageId = "in-flight", ReceiptHandle = "r1", Body = "test" };

        var mockClient = new Mock<IAmazonSQS>();
        mockClient
            .Setup(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = new List<Message> { message } });

        var mockPipeline = new Mock<IMiddlewarePipeline<SqsConsumerMessageContext>>();
        mockPipeline
            .Setup(x => x.HandleAsync(It.IsAny<SqsConsumerMessageContext>(), It.IsAny<IServiceResolver>()))
            .Returns((SqsConsumerMessageContext context, IServiceResolver resolver) =>
            {
                var token = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                cts.Cancel(); // shutdown fires mid-handler
                throw new OperationCanceledException(token);
            });

        var sqsConsumerApplication = new SqsConsumerApplication(mockPipeline.Object, new SqsConsumerOptions { AckMode = SqsConsumerAckMode.PerMessage });

        var mockClientFactory = new Mock<ISqsClientFactory>();
        mockClientFactory.Setup(x => x.Create()).Returns(mockClient.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<CancellationTokenAccessor>();
        services.AddScoped<ICancellationTokenAccessor>(x => x.GetService<CancellationTokenAccessor>());
        var resolverFactory = new MicrosoftServiceResolverFactory(services);

        var consumer = new SqsConsumer(resolverFactory, sqsConsumerApplication,
            new SqsConsumerConfig { QueueUrl = "https://example/queue", MaxNumberOfMessages = 10 },
            mockClientFactory.Object);

        await consumer.StartAsync(cts.Token);

        mockClient.Verify(x => x.DeleteMessageBatchAsync(It.IsAny<DeleteMessageBatchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        mockClient.Verify(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
