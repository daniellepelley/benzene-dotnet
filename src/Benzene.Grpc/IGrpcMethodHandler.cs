using Grpc.Core;

namespace Benzene.Grpc;

/// <summary>Runs one routed gRPC call, of any call shape, through the Benzene message pipeline.</summary>
public interface IGrpcMethodHandler
{
    /// <summary>Handles a unary call.</summary>
    Task<TResponse> HandleAsync<TRequest, TResponse>(TRequest request, ServerCallContext context)
        where TRequest : class
        where TResponse : class;

    /// <summary>Handles a server-streaming call.</summary>
    Task ServerStreamingAsync<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context)
        where TRequest : class
        where TResponse : class;

    /// <summary>Handles a client-streaming call.</summary>
    Task<TResponse> ClientStreamingAsync<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context)
        where TRequest : class
        where TResponse : class;

    /// <summary>Handles a duplex-streaming call.</summary>
    Task DuplexStreamingAsync<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context)
        where TRequest : class
        where TResponse : class;
}
