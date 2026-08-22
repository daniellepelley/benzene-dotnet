using Benzene.Abstractions.Middleware;
using Benzene.Grpc.Serialization;
using Grpc.Core;

namespace Benzene.Grpc.Client;

/// <summary>Terminal middleware that invokes the routed gRPC method for the context's topic and records the outcome.</summary>
public class GrpcClientMiddleware : IMiddleware<GrpcSendMessageContext>, ITerminalMiddleware
{
    private readonly CallInvoker _callInvoker;
    private readonly IGrpcClientRouteRegistry _routeRegistry;
    private readonly IGrpcMessageAdapter _adapter;

    /// <summary>Initializes a new instance of the <see cref="GrpcClientMiddleware"/> class.</summary>
    /// <param name="callInvoker">The gRPC call invoker to call with.</param>
    /// <param name="routeRegistry">Resolves a Benzene topic to its gRPC method.</param>
    /// <param name="adapter">Converts between the wire protobuf request/response and the caller's declared types.</param>
    public GrpcClientMiddleware(CallInvoker callInvoker, IGrpcClientRouteRegistry routeRegistry, IGrpcMessageAdapter adapter)
    {
        _callInvoker = callInvoker;
        _routeRegistry = routeRegistry;
        _adapter = adapter;
    }

    /// <summary>Gets the name of this middleware.</summary>
    public string Name => nameof(GrpcClientMiddleware);

    /// <summary>Invokes the routed gRPC method and records its status/trailers. Terminal middleware; does not call <paramref name="next"/>.</summary>
    public async Task HandleAsync(GrpcSendMessageContext context, Func<Task> next)
    {
        var route = _routeRegistry.Find(context.Topic);
        if (route == null)
        {
            context.Status = new Status(StatusCode.Unimplemented, $"No gRPC route has been registered for topic '{context.Topic}'.");
            return;
        }

        try
        {
            await route.InvokeAsync(_callInvoker, _adapter, context);
        }
        catch (RpcException ex)
        {
            context.Status = ex.Status;
            context.ResponseTrailers = ex.Trailers;
        }
    }
}
