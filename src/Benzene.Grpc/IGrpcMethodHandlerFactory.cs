namespace Benzene.Grpc;

/// <summary>Creates an <see cref="IGrpcMethodHandler"/> for a routed gRPC method.</summary>
public interface IGrpcMethodHandlerFactory
{
    /// <summary>Creates the handler for <paramref name="grpcMethodDefinition"/>.</summary>
    IGrpcMethodHandler Create(IGrpcMethodDefinition grpcMethodDefinition);
}