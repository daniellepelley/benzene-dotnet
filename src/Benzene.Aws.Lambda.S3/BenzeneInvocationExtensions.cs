using System;
using System.Collections.Generic;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.S3;

/// <summary>
/// Provides the per-record <see cref="IBenzeneInvocation"/> for the S3 pipeline.
/// </summary>
public static class BenzeneInvocationExtensions
{
    /// <summary>
    /// Adds middleware that exposes an <see cref="IBenzeneInvocation"/> for the duration of each S3
    /// record's dispatch, with <see cref="IBenzeneInvocation.InvocationId"/> set to the record's
    /// <c>responseElements.x-amz-request-id</c> (falling back to the object key, then a generated id,
    /// if that's absent - e.g. in hand-built test payloads).
    /// </summary>
    /// <remarks>
    /// Each S3 record is dispatched through its own DI scope (<c>S3Application</c>'s per-record
    /// <c>serviceResolverFactory.CreateScope()</c>), which doesn't inherit whatever
    /// <see cref="IBenzeneInvocation"/> was populated for the whole Lambda invocation - see
    /// <see cref="Benzene.Aws.Lambda.Sqs.BenzeneInvocationExtensions.UseBenzeneInvocation(IMiddlewarePipelineBuilder{Benzene.Aws.Lambda.Sqs.SqsMessageContext})"/>
    /// for the full rationale (identical shape, SQS side). Auto-wired by <c>UseS3(...)</c> as the
    /// first middleware in the S3 pipeline, so no application code changes are required.
    /// </remarks>
    /// <param name="app">The pipeline builder to add the invocation middleware to.</param>
    /// <returns>The pipeline builder, for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<S3RecordContext> UseBenzeneInvocation(
        this IMiddlewarePipelineBuilder<S3RecordContext> app)
    {
        return app.UseBenzeneInvocation((_, context) =>
        {
            var record = context.S3EventNotificationRecord;
            var invocationId = record?.ResponseElements?.XAmzRequestId
                ?? record?.S3?.Object?.Key
                ?? Guid.NewGuid().ToString();

            return new BenzeneInvocation(invocationId, Benzene.Aws.Lambda.Core.BenzeneInvocationExtensions.PlatformName, new Dictionary<Type, object>());
        });
    }
}
