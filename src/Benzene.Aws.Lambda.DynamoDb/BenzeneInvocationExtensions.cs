using System;
using System.Collections.Generic;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.DynamoDb;

/// <summary>
/// Provides the per-record <see cref="IBenzeneInvocation"/> for the DynamoDB Streams pipeline.
/// </summary>
public static class BenzeneInvocationExtensions
{
    /// <summary>
    /// Adds middleware that exposes an <see cref="IBenzeneInvocation"/> for the duration of each
    /// stream record's dispatch, with <see cref="IBenzeneInvocation.InvocationId"/> set to the
    /// record's <c>eventID</c>.
    /// </summary>
    /// <remarks>
    /// Each stream record is dispatched through its own DI scope (<c>DynamoDbApplication</c>'s
    /// per-record <c>serviceResolverFactory.CreateScope()</c>), which doesn't inherit whatever
    /// <see cref="IBenzeneInvocation"/> was populated for the whole Lambda invocation - see
    /// <see cref="Benzene.Aws.Lambda.Sqs.BenzeneInvocationExtensions.UseBenzeneInvocation(IMiddlewarePipelineBuilder{Benzene.Aws.Lambda.Sqs.SqsMessageContext})"/>
    /// for the full rationale (identical shape, SQS side). Auto-wired by <c>UseDynamoDb(...)</c> as
    /// the first middleware in the DynamoDB pipeline, so no application code changes are required.
    /// </remarks>
    /// <param name="app">The pipeline builder to add the invocation middleware to.</param>
    /// <returns>The pipeline builder, for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<DynamoDbRecordContext> UseBenzeneInvocation(
        this IMiddlewarePipelineBuilder<DynamoDbRecordContext> app)
    {
        return app.UseBenzeneInvocation((_, context) =>
            new BenzeneInvocation(
                context.Record?.EventId ?? Guid.NewGuid().ToString(),
                Benzene.Aws.Lambda.Core.BenzeneInvocationExtensions.PlatformName,
                new Dictionary<Type, object>()));
    }
}
