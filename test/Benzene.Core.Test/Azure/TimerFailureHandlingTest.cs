using System;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Timer;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Azure;

/// <summary>
/// Regression coverage for #231: <see cref="TimerApplication"/> (via <see cref="TimerTickApplication"/>)
/// must escalate a message-handler's returned failure result into a thrown
/// <see cref="TimerMessageProcessingException"/>, matching every sibling Azure Function trigger's
/// safe-by-default <c>RaiseOnFailureStatus</c> contract - a gap the timer package previously had none
/// of (a tick whose handler returned <c>BenzeneResult.UnexpectedError()</c> completed silently).
/// </summary>
public class TimerFailureHandlingTest
{
    private static Mock<IServiceResolverFactory> CreateResolverFactory()
    {
        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<TimerApplication>>()).Returns(Mock.Of<ILogger<TimerApplication>>());
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);
        return mockResolverFactory;
    }

    [Fact]
    public void TimerOptions_Defaults_CascadeExceptions_AndEscalateFailureResults()
    {
        var options = new TimerOptions();
        Assert.False(options.CatchExceptions);
        Assert.True(options.RaiseOnFailureStatus);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerThrows_ExceptionCascades()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<TimerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<TimerContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var application = new TimerTickApplication(mockPipeline.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.HandleAsync(new TimerTriggerInfo(), CreateResolverFactory().Object));
    }

    /// <summary>
    /// The review's exact probe: a handler sets <c>BenzeneResult.UnexpectedError()</c> rather than
    /// throwing. Before the fix the invocation completed silently (the Azure Functions host saw a
    /// successful invocation); after the fix, with the default options, it throws.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerReturnsFailureResult_ThrowsTimerMessageProcessingException()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<TimerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<TimerContext>(), It.IsAny<IServiceResolver>()))
            .Callback<TimerContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var application = new TimerTickApplication(mockPipeline.Object);

        await Assert.ThrowsAsync<TimerMessageProcessingException>(
            () => application.HandleAsync(new TimerTriggerInfo(), CreateResolverFactory().Object));
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusFalse_HandlerReturnsFailureResult_CompletesWithoutThrowing()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<TimerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<TimerContext>(), It.IsAny<IServiceResolver>()))
            .Callback<TimerContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var application = new TimerTickApplication(mockPipeline.Object, new TimerOptions { RaiseOnFailureStatus = false });

        // Explicitly opting out must complete cleanly - no exception.
        await application.HandleAsync(new TimerTriggerInfo(), CreateResolverFactory().Object);
    }

    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_HandlerThrows_ExceptionIsLoggedNotThrown()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<TimerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<TimerContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var application = new TimerTickApplication(mockPipeline.Object, new TimerOptions { CatchExceptions = true });

        await application.HandleAsync(new TimerTriggerInfo(), CreateResolverFactory().Object);
    }

    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_EscalatedFailureResult_IsLoggedNotThrown()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<TimerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<TimerContext>(), It.IsAny<IServiceResolver>()))
            .Callback<TimerContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var application = new TimerTickApplication(mockPipeline.Object, new TimerOptions { CatchExceptions = true });

        // CatchExceptions also contains the escalation throw itself, not only the pipeline's own exceptions.
        await application.HandleAsync(new TimerTriggerInfo(), CreateResolverFactory().Object);
    }

    /// <summary>
    /// Regression coverage for #257: under <c>CatchExceptions = true</c>, an infrastructure/DI-wiring
    /// failure (<see cref="BenzeneFailure.IsInfrastructure"/>) is not this tick's fault - it will fail
    /// identically for every tick - so it must escape containment and fail the invocation loudly,
    /// mirroring <c>SingleContextEscalatingApplicationBase.ProcessAsync</c>'s #228 fix. Before the fix,
    /// this completed without throwing (logged only), exactly the "whole invocation reports success
    /// while every tick fails the same way, forever" defect #228 fixed for AWS SNS/S3/EventBridge.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_InfrastructureFailure_EscapesContainmentAndRethrows()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<TimerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<TimerContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new BenzeneResolutionException("Unable to resolve ISomeService"));

        var application = new TimerTickApplication(mockPipeline.Object, new TimerOptions { CatchExceptions = true });

        await Assert.ThrowsAsync<BenzeneResolutionException>(
            () => application.HandleAsync(new TimerTriggerInfo(), CreateResolverFactory().Object));
    }
}
