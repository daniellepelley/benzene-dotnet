using System;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Benzene.Abstractions.DI;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.AwsEventStream;

namespace Benzene.Aws.Lambda.HttpBridge;

/// <summary>
/// Claims HTTP-shaped invocations out of the AWS event-stream pipeline and hands them to a bridge.
/// </summary>
/// <typeparam name="TRequest">The API Gateway/ALB request payload type.</typeparam>
/// <typeparam name="TResponse">The matching response payload type.</typeparam>
/// <remarks>
/// Sits in the same chain as <c>SqsLambdaHandler</c>, <c>SnsLambdaHandler</c> and the rest: it tries
/// to deserialize the invocation into <typeparamref name="TRequest"/>, asks whether the result really
/// is its event shape, and either handles it or defers to the next middleware. It replaces Benzene's
/// own API Gateway binding rather than sitting alongside it — a function serves a given HTTP payload
/// shape from one place or the other, not both.
/// </remarks>
public class HttpBridgeLambdaHandler<TRequest, TResponse> : AwsLambdaMiddlewareRouter<TRequest>
{
    private readonly Func<TRequest, ILambdaContext, Task<TResponse>> _handle;
    private readonly Func<TRequest, bool> _canHandle;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpBridgeLambdaHandler{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="serviceResolver">The service resolver for the current invocation scope.</param>
    /// <param name="canHandle">Decides whether a deserialized payload really is this event shape.</param>
    /// <param name="handle">Hands the request to the bridged application.</param>
    public HttpBridgeLambdaHandler(
        IServiceResolver serviceResolver,
        Func<TRequest, bool> canHandle,
        Func<TRequest, ILambdaContext, Task<TResponse>> handle)
        : base(serviceResolver)
    {
        _canHandle = canHandle;
        _handle = handle;
    }

    /// <inheritdoc />
    protected override bool CanHandle(TRequest request) => request != null && _canHandle(request);

    /// <inheritdoc />
    protected override async Task HandleFunction(
        TRequest request, AwsEventStreamContext context, IServiceResolverFactory serviceResolverFactory)
    {
        MapResponse(context, await _handle(request, context.LambdaContext));
    }
}
