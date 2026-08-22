using Google.Protobuf;

namespace Benzene.Grpc.Client;

/// <summary>Maps an outbound Benzene topic to the gRPC method that serves it, for <see cref="GrpcBenzeneMessageClient"/>.</summary>
public interface IGrpcClientRouteRegistry
{
    /// <summary>
    /// Registers a unary gRPC call under <paramref name="topic"/>. <typeparamref name="TRequest"/> and
    /// <typeparamref name="TResponse"/> are the RPC's protobuf wire types; <paramref name="fullMethodName"/>
    /// is the gRPC method's fully-qualified path, e.g. <c>/benzene.test.TestService/Echo</c>.
    /// </summary>
    IGrpcClientRouteRegistry Add<TRequest, TResponse>(string topic, string fullMethodName)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>;

    /// <summary>Finds the registered route for <paramref name="topic"/>, or <c>null</c> if none is registered.</summary>
    IGrpcClientRoute? Find(string topic);
}
