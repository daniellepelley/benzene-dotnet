using Benzene.Core.Exceptions;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Benzene.Grpc;

/// <summary>
/// A gRPC server-side interceptor that routes a call through Benzene's message-handler pipeline when
/// its method has a registered route, falling through to the call's own continuation otherwise (e.g.
/// a health-check or reflection service method Benzene doesn't own).
/// </summary>
public class BenzeneInterceptor : Interceptor
{
    private readonly IGrpcMethodHandlerFactoryAccessor _grpcMethodHandlerFactoryAccessor;
    private readonly IGrpcRouteFinder _grpcRouteFinder;

    /// <summary>Initializes a new instance of the <see cref="BenzeneInterceptor"/> class.</summary>
    /// <param name="grpcMethodHandlerFactoryAccessor">Accesses the configured pipeline's method-handler factory.</param>
    /// <param name="grpcRouteFinder">Resolves a gRPC method to its Benzene topic, if routed.</param>
    public BenzeneInterceptor(IGrpcMethodHandlerFactoryAccessor grpcMethodHandlerFactoryAccessor, IGrpcRouteFinder grpcRouteFinder)
    {
        _grpcRouteFinder = grpcRouteFinder;
        _grpcMethodHandlerFactoryAccessor = grpcMethodHandlerFactoryAccessor;
    }

    /// <inheritdoc />
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var handler = TryCreateHandler(context);
        if (handler != null)
        {
            return base.UnaryServerHandler(request, context, handler.HandleAsync<TRequest, TResponse>);
        }

        return base.UnaryServerHandler(request, context, continuation);
    }

    /// <inheritdoc />
    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var handler = TryCreateHandler(context);
        if (handler != null)
        {
            return base.ClientStreamingServerHandler(requestStream, context, handler.ClientStreamingAsync<TRequest, TResponse>);
        }

        return base.ClientStreamingServerHandler(requestStream, context, continuation);
    }

    /// <inheritdoc />
    public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var handler = TryCreateHandler(context);
        if (handler != null)
        {
            return base.ServerStreamingServerHandler(request, responseStream, context, handler.ServerStreamingAsync<TRequest, TResponse>);
        }

        return base.ServerStreamingServerHandler(request, responseStream, context, continuation);
    }

    /// <inheritdoc />
    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var handler = TryCreateHandler(context);
        if (handler != null)
        {
            return base.DuplexStreamingServerHandler(requestStream, responseStream, context, handler.DuplexStreamingAsync<TRequest, TResponse>);
        }

        return base.DuplexStreamingServerHandler(requestStream, responseStream, context, continuation);
    }

    private IGrpcMethodHandler? TryCreateHandler(ServerCallContext context)
    {
        var topic = _grpcRouteFinder.Find(context.Method);
        if (topic == null)
        {
            return null;
        }

        var factory = _grpcMethodHandlerFactoryAccessor.Factory
            ?? throw new BenzeneException("No gRPC pipeline has been configured; call UseGrpc before handling requests.");
        return factory.Create(topic);
    }
}
