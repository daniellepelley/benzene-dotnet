using System;
using System.Collections.Generic;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.EventBridge;

/// <summary>
/// Provides the per-event <see cref="IBenzeneInvocation"/> for the EventBridge pipeline.
/// </summary>
public static class BenzeneInvocationExtensions
{
    /// <summary>
    /// Adds middleware that exposes an <see cref="IBenzeneInvocation"/> for the duration of the
    /// event's dispatch, with <see cref="IBenzeneInvocation.InvocationId"/> set to the event's
    /// <c>id</c>.
    /// </summary>
    /// <remarks>
    /// EventBridge dispatches its single event through its own DI scope
    /// (<c>EventBridgeApplication</c>'s per-event <c>serviceResolverFactory.CreateScope()</c>), which
    /// doesn't inherit whatever <see cref="IBenzeneInvocation"/> was populated for the whole Lambda
    /// invocation - see
    /// <see cref="Benzene.Aws.Lambda.Sqs.BenzeneInvocationExtensions.UseBenzeneInvocation(IMiddlewarePipelineBuilder{Benzene.Aws.Lambda.Sqs.SqsMessageContext})"/>
    /// for the full rationale (identical shape, SQS side - one event or one record, both dispatched
    /// through a fresh scope the outer invocation's populated <see cref="IBenzeneInvocation"/> never
    /// reaches). Auto-wired by <c>UseEventBridge(...)</c> as the first middleware in the EventBridge
    /// pipeline, so no application code changes are required.
    /// </remarks>
    /// <param name="app">The pipeline builder to add the invocation middleware to.</param>
    /// <returns>The pipeline builder, for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<EventBridgeContext> UseBenzeneInvocation(
        this IMiddlewarePipelineBuilder<EventBridgeContext> app)
    {
        return app.UseBenzeneInvocation((_, context) =>
            new BenzeneInvocation(
                context.Event?.Id ?? Guid.NewGuid().ToString(),
                Benzene.Aws.Lambda.Core.BenzeneInvocationExtensions.PlatformName,
                new Dictionary<Type, object>()));
    }
}
