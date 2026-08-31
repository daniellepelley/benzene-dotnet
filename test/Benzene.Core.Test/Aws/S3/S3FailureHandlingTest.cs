using System;
using Benzene.Results;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.Lambda.S3Events;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.S3;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.S3;

public class S3FailureHandlingTest
{
    private static S3Event CreateEvent(string key = "object-1")
    {
        return new S3Event
        {
            Records = new List<S3Event.S3EventNotificationRecord>
            {
                new S3Event.S3EventNotificationRecord
                {
                    EventSource = "aws:s3",
                    S3 = new S3Event.S3Entity { Object = new S3Event.S3ObjectEntity { Key = key } }
                }
            }
        };
    }

    private static Mock<IServiceResolverFactory> CreateResolverFactory()
    {
        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<S3Application>>()).Returns(Mock.Of<ILogger<S3Application>>());
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);
        return mockResolverFactory;
    }

    [Fact]
    public void S3Options_Defaults_CascadeExceptions_AndEscalateFailureResults()
    {
        var options = new S3Options();
        Assert.False(options.CatchExceptions);
        Assert.True(options.RaiseOnFailureStatus);
        Assert.Null(options.MaxDegreeOfParallelism);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerThrows_ExceptionCascades()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<S3RecordContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<S3RecordContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var application = new S3Application(mockPipeline.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(CreateEvent(), CreateResolverFactory().Object));
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_HandlerReturnsFailureResult_ThrowsS3MessageProcessingException()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<S3RecordContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<S3RecordContext>(), It.IsAny<IServiceResolver>()))
            .Callback<S3RecordContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var application = new S3Application(mockPipeline.Object, new S3Options { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<S3MessageProcessingException>(
            () => application.HandleAsync(CreateEvent("object-2"), CreateResolverFactory().Object));
        Assert.Equal("object-2", exception.ObjectKey);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_NoResultRecorded_ThrowsS3MessageProcessingException()
    {
        // Nothing set a MessageResult - typically an unrouted record (no handler matched the topic).
        // Per work/settlement-consistency-fix-plan.md row 2, a null outcome is escalated the same as an
        // explicit failure result, not accepted as success - S3's own async-invoke retry + on-failure
        // destination is the backstop that makes retaining it safe.
        var mockPipeline = new Mock<IMiddlewarePipeline<S3RecordContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<S3RecordContext>(), It.IsAny<IServiceResolver>()))
            .Returns(Task.CompletedTask);

        var application = new S3Application(mockPipeline.Object, new S3Options { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<S3MessageProcessingException>(
            () => application.HandleAsync(CreateEvent("object-3"), CreateResolverFactory().Object));
        Assert.Equal("object-3", exception.ObjectKey);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerReturnsFailureResult_ThrowsS3MessageProcessingException()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<S3RecordContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<S3RecordContext>(), It.IsAny<IServiceResolver>()))
            .Callback<S3RecordContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var application = new S3Application(mockPipeline.Object);

        // Safe-by-default: a returned failure result is escalated so AWS retries it, not silently accepted.
        await Assert.ThrowsAsync<S3MessageProcessingException>(
            () => application.HandleAsync(CreateEvent(), CreateResolverFactory().Object));
    }

    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_InfrastructureFailure_RethrowsDespiteCatchExceptions()
    {
        // #228: an infrastructure/DI-wiring failure (BenzeneResolutionException) fails the whole
        // invocation even when CatchExceptions opts into swallowing ordinary handler exceptions - S3
        // has no partial-failure channel, so swallowing this means 100%-loss-reported-as-success.
        var mockPipeline = new Mock<IMiddlewarePipeline<S3RecordContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<S3RecordContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new BenzeneResolutionException("Unable to resolve type IExampleService"));

        var application = new S3Application(mockPipeline.Object, new S3Options { CatchExceptions = true });

        await Assert.ThrowsAsync<BenzeneResolutionException>(() => application.HandleAsync(CreateEvent(), CreateResolverFactory().Object));
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerNeverSetsMessageResult_ThrowsS3MessageProcessingException()
    {
        // #229: a null/unset MessageResult is treated as a failure, not a silent success, aligning
        // with SQS/DynamoDb's "err toward redelivery, never toward loss" convention.
        var mockPipeline = new Mock<IMiddlewarePipeline<S3RecordContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<S3RecordContext>(), It.IsAny<IServiceResolver>()))
            .Returns(Task.CompletedTask);

        var application = new S3Application(mockPipeline.Object);

        var exception = await Assert.ThrowsAsync<S3MessageProcessingException>(
            () => application.HandleAsync(CreateEvent("object-3"), CreateResolverFactory().Object));
        Assert.Equal("object-3", exception.ObjectKey);
    }
}
