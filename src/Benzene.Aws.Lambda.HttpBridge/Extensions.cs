using System;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.HttpBridge;

/// <summary>
/// Registers an HTTP bridge on the AWS event-stream pipeline, so a Lambda can serve HTTP through an
/// application Benzene does not own while handling queue and event traffic itself.
/// </summary>
/// <remarks>
/// <para>
/// Use these <em>instead of</em> <c>UseApiGateway</c>/<c>UseApiGatewayV2</c>, not alongside them:
/// both claim the same payload shapes, and a function serves a given shape from one place or the
/// other. Everything non-HTTP is unaffected — <c>UseSqs</c>, <c>UseSns</c> and the rest chain on as
/// usual.
/// </para>
/// <para>
/// The detection rules here are the same ones Benzene's own API Gateway routers use, so a bridged
/// function claims exactly the events an unbridged one would.
/// </para>
/// </remarks>
public static class Extensions
{
    /// <summary>
    /// Bridges API Gateway HTTP API (payload format 2.0) invocations to an external HTTP application.
    /// </summary>
    /// <param name="app">The AWS event-stream pipeline builder.</param>
    /// <param name="handle">Hands the request to the bridged application.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    /// <example>
    /// <code>
    /// app.UseHttpBridgeV2((request, context) =&gt; aspNetFunction.FunctionHandlerAsync(request, context))
    ///    .UseSqs(sqs =&gt; sqs.UseMessageHandlers());
    /// </code>
    /// </example>
    public static IMiddlewarePipelineBuilder<AwsEventStreamContext> UseHttpBridgeV2(
        this IMiddlewarePipelineBuilder<AwsEventStreamContext> app,
        Func<APIGatewayHttpApiV2ProxyRequest, ILambdaContext, Task<APIGatewayHttpApiV2ProxyResponse>> handle)
    {
        return app.Use(resolver =>
            new HttpBridgeLambdaHandler<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>(
                resolver, IsHttpApiV2, handle));
    }

    /// <summary>
    /// Bridges API Gateway HTTP API (payload format 2.0) invocations to a registered
    /// <see cref="IAwsHttpBridge{TRequest, TResponse}"/>, resolved per invocation.
    /// </summary>
    /// <param name="app">The AWS event-stream pipeline builder.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<AwsEventStreamContext> UseHttpBridgeV2(
        this IMiddlewarePipelineBuilder<AwsEventStreamContext> app)
    {
        return app.Use(resolver =>
            new HttpBridgeLambdaHandler<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>(
                resolver,
                IsHttpApiV2,
                (request, context) => resolver
                    .GetService<IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>>()
                    .HandleAsync(request, context)));
    }

    /// <summary>
    /// Bridges API Gateway REST/HTTP API (payload format 1.0) invocations to an external HTTP application.
    /// </summary>
    /// <param name="app">The AWS event-stream pipeline builder.</param>
    /// <param name="handle">Hands the request to the bridged application.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<AwsEventStreamContext> UseHttpBridge(
        this IMiddlewarePipelineBuilder<AwsEventStreamContext> app,
        Func<APIGatewayProxyRequest, ILambdaContext, Task<APIGatewayProxyResponse>> handle)
    {
        return app.Use(resolver =>
            new HttpBridgeLambdaHandler<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                resolver, IsRestApi, handle));
    }

    /// <summary>
    /// Bridges API Gateway REST/HTTP API (payload format 1.0) invocations to a registered
    /// <see cref="IAwsHttpBridge{TRequest, TResponse}"/>, resolved per invocation.
    /// </summary>
    /// <param name="app">The AWS event-stream pipeline builder.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<AwsEventStreamContext> UseHttpBridge(
        this IMiddlewarePipelineBuilder<AwsEventStreamContext> app)
    {
        return app.Use(resolver =>
            new HttpBridgeLambdaHandler<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                resolver,
                IsRestApi,
                (request, context) => resolver
                    .GetService<IAwsHttpBridge<APIGatewayProxyRequest, APIGatewayProxyResponse>>()
                    .HandleAsync(request, context)));
    }

    // Same rules as ApiGatewayV2LambdaHandler/ApiGatewayLambdaHandler, so bridging changes who serves
    // an event and never which events are served.
    private static bool IsHttpApiV2(APIGatewayHttpApiV2ProxyRequest request)
        => request.Version == "2.0" || request.RequestContext?.Http?.Method != null;

    private static bool IsRestApi(APIGatewayProxyRequest request)
        => request.HttpMethod != null;
}
