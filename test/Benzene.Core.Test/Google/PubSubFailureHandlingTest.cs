using System;
using Benzene.Results;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers;
using Benzene.GoogleCloud.Functions.PubSub;
using Benzene.GoogleCloud.Functions.PubSub.TestHelpers;
using Benzene.Test.Logging.Helpers;
using Google.Events.Protobuf.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Google;

public class PubSubFailureHandlingTest
{
    private static MessagePublishedData CreateData(string messageId = "msg-1")
    {
        return new PubSubMessageBuilder().WithMessageId(messageId).Build();
    }

    private static (Mock<IServiceResolver> Resolver, Mock<IServiceResolverFactory> ResolverFactory) CreateResolver()
    {
        var mockLogger = new Mock<ILogger<PubSubMiddlewareApplication>>();
        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<PubSubMiddlewareApplication>>()).Returns(mockLogger.Object);
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);
        return (mockResolver, mockResolverFactory);
    }

    [Fact]
    public void PubSubOptions_Defaults_CascadeExceptions_AndEscalateFailureResults()
    {
        var options = new PubSubOptions();
        Assert.False(options.CatchExceptions);
        Assert.True(options.RaiseOnFailureStatus);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerThrows_ExceptionCascades()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, resolverFactory) = CreateResolver();
        var application = new PubSubMiddlewareApplication(mockPipeline.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(CreateData(), resolverFactory.Object));
    }

    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_HandlerThrows_ExceptionIsSwallowedAndLogged()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, resolverFactory) = CreateResolver();
        var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { CatchExceptions = true });

        // Reaching the end without throwing proves the exception was caught, not cascaded.
        await application.HandleAsync(CreateData(), resolverFactory.Object);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_HandlerReturnsFailureResult_ThrowsPubSubMessageProcessingException()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .Callback<PubSubContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<PubSubMessageProcessingException>(
            () => application.HandleAsync(CreateData("msg-2"), resolverFactory.Object));
        Assert.Equal("msg-2", exception.MessageId);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_NoResultRecorded_ThrowsPubSubMessageProcessingException()
    {
        // Nothing set a MessageResult - typically an unrouted message (no handler matched the topic).
        // Per work/settlement-consistency-fix-plan.md row 6, a null outcome is escalated the same as
        // an explicit failure result, not accepted as success - Pub/Sub's own dead-letter topic support
        // is the backstop that makes retaining it safe.
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<PubSubMessageProcessingException>(
            () => application.HandleAsync(CreateData("msg-3"), resolverFactory.Object));
        Assert.Equal("msg-3", exception.MessageId);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_HandlerSucceeds_DoesNotThrow()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .Callback<PubSubContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { RaiseOnFailureStatus = true });

        await application.HandleAsync(CreateData(), resolverFactory.Object);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusAndCatchExceptionsBothTrue_FailureResultIsEscalatedThenSwallowed()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .Callback<PubSubContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { RaiseOnFailureStatus = true, CatchExceptions = true });

        // Reaching the end without throwing proves the escalated failure was caught too.
        await application.HandleAsync(CreateData(), resolverFactory.Object);
    }

    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_NoMessageOnEvent_LogsTheRealExceptionNotAnNre()
    {
        // Regression test for the NRE-masking bug: previously the catch block's own logging call
        // dereferenced context.Message.MessageId unconditionally, so a CloudEvent with no Pub/Sub
        // message replaced whatever real exception the pipeline threw with an unrelated
        // NullReferenceException before it ever reached the logger.
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        var realException = new InvalidOperationException("boom");
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(realException);

        var loggerFactory = new FakeLoggerFactory();
        var logger = new FakeLogger<PubSubMiddlewareApplication>(loggerFactory.Collector);
        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<PubSubMiddlewareApplication>>()).Returns(logger);
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);

        var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { CatchExceptions = true });
        var noMessageData = new PubSubMessageBuilder().BuildWithNoMessage();

        await application.HandleAsync(noMessageData, mockResolverFactory.Object);

        var entry = Assert.Single(loggerFactory.Collector.Entries);
        Assert.Same(realException, entry.Exception);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_NoMessageOnEvent_ThrowsWithNullMessageIdInsteadOfNre()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<PubSubContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<PubSubContext>(), It.IsAny<IServiceResolver>()))
            .Callback<PubSubContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { RaiseOnFailureStatus = true });
        var noMessageData = new PubSubMessageBuilder().BuildWithNoMessage();

        var exception = await Assert.ThrowsAsync<PubSubMessageProcessingException>(
            () => application.HandleAsync(noMessageData, resolverFactory.Object));
        Assert.Null(exception.MessageId);
    }
}
