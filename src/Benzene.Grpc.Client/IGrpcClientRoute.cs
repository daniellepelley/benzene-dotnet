using Benzene.Grpc.Serialization;
using Grpc.Core;

namespace Benzene.Grpc.Client;

/// <summary>
/// A registered gRPC client call, closed over its request/response protobuf wire types. Bridges the
/// topic-addressed, boxed-payload world of <see cref="GrpcSendMessageContext"/> to a strongly-typed
/// <see cref="Method{TRequest,TResponse}"/> invocation via <see cref="CallInvoker"/>.
/// </summary>
public interface IGrpcClientRoute
{
    /// <summary>Invokes the route's gRPC method for <paramref name="context"/> and records the outcome on it.</summary>
    /// <param name="invoker">The gRPC call invoker to call with.</param>
    /// <param name="adapter">Converts between the wire protobuf request/response and the caller's declared types.</param>
    /// <param name="context">The outbound send context carrying the message to convert and send.</param>
    Task InvokeAsync(CallInvoker invoker, IGrpcMessageAdapter adapter, GrpcSendMessageContext context);
}
