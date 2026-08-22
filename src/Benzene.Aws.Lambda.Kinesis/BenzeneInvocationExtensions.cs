using System;
using System.Collections.Generic;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.Kinesis;

/// <summary>
/// Provides the per-batch <see cref="IBenzeneInvocation"/> for the Kinesis streaming pipeline.
/// </summary>
public static class BenzeneInvocationExtensions
{
    /// <summary>
    /// Adds middleware that exposes an <see cref="IBenzeneInvocation"/> for the duration of the
    /// batch's dispatch, with <see cref="IBenzeneInvocation.InvocationId"/> set to a freshly generated
    /// id.
    /// </summary>
    /// <remarks>
    /// Unlike the fan-out transports (SQS/SNS/S3/DynamoDB/Kafka), Kinesis presents the whole batch to
    /// the pipeline as a single <see cref="StreamContext{TItem}"/> (fan-in, one scope per invocation -
    /// see <c>KinesisStreamApplication</c>), so there is no natural per-record id to key this on; a
    /// batch can also be empty. This middleware still needs to run because that single scope is,
    /// exactly like the fan-out transports' per-record scopes, created fresh by
    /// <c>MiddlewareApplication{TEvent,TContext,TResult}</c>'s own
    /// <c>serviceResolverFactory.CreateScope()</c> and so doesn't inherit whatever
    /// <see cref="IBenzeneInvocation"/> was populated for the whole Lambda invocation - see
    /// <see cref="Benzene.Aws.Lambda.Sqs.BenzeneInvocationExtensions.UseBenzeneInvocation(IMiddlewarePipelineBuilder{Benzene.Aws.Lambda.Sqs.SqsMessageContext})"/>
    /// for the full rationale (identical underlying cause, different batch shape). Auto-wired by
    /// <c>UseKinesisStream(...)</c> as the first middleware in the Kinesis pipeline, so no application
    /// code changes are required.
    /// </remarks>
    /// <param name="app">The pipeline builder to add the invocation middleware to.</param>
    /// <returns>The pipeline builder, for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<StreamContext<KinesisEventRecord>> UseBenzeneInvocation(
        this IMiddlewarePipelineBuilder<StreamContext<KinesisEventRecord>> app)
    {
        return app.UseBenzeneInvocation((_, _) =>
            new BenzeneInvocation(
                Guid.NewGuid().ToString(),
                Benzene.Aws.Lambda.Core.BenzeneInvocationExtensions.PlatformName,
                new Dictionary<Type, object>()));
    }
}
