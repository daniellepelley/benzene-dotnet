using System;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.DynamoDb;

/// <summary>
/// Provides extension methods for adding DynamoDB Streams handling to an AWS Lambda middleware pipeline.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds DynamoDB Streams handling to the pipeline.
    /// </summary>
    /// <param name="app">The AWS event stream pipeline builder to add DynamoDB handling to.</param>
    /// <param name="action">The action that configures the inner DynamoDB pipeline.</param>
    /// <returns>The pipeline builder for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<AwsEventStreamContext> UseDynamoDb(this IMiddlewarePipelineBuilder<AwsEventStreamContext> app, Action<IMiddlewarePipelineBuilder<DynamoDbRecordContext>> action)
    {
        return app.UseDynamoDb(action, _ => { });
    }

    /// <summary>
    /// Adds DynamoDB Streams handling to the pipeline, with settlement options - e.g.
    /// <see cref="DynamoDbOptions.AtMostOnce"/>, the at-most-once override.
    /// </summary>
    /// <param name="app">The AWS event stream pipeline builder to add DynamoDB handling to.</param>
    /// <param name="action">The action that configures the inner DynamoDB pipeline.</param>
    /// <param name="configure">Configures how a batch settles.</param>
    /// <returns>The pipeline builder for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<AwsEventStreamContext> UseDynamoDb(this IMiddlewarePipelineBuilder<AwsEventStreamContext> app, Action<IMiddlewarePipelineBuilder<DynamoDbRecordContext>> action, Action<DynamoDbOptions> configure)
    {
        app.Register(x => x.AddDynamoDb());
        var pipeline = app.CreateMiddlewarePipeline<DynamoDbRecordContext>(builder =>
        {
            builder.UseBenzeneInvocation();
            action(builder);
        });
        var options = new DynamoDbOptions();
        configure(options);
        return app.Use(resolver => new DynamoDbLambdaHandler(new DynamoDbApplication(pipeline, options), resolver));
    }
}
