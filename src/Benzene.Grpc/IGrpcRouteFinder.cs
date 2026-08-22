namespace Benzene.Grpc;

/// <summary>Resolves a gRPC method path to its <see cref="IGrpcMethodDefinition"/>, if routed.</summary>
public interface IGrpcRouteFinder
{
    /// <summary>Finds the definition for <paramref name="method"/>, or <c>null</c> if it isn't routed to a Benzene handler.</summary>
    IGrpcMethodDefinition? Find(string method);
}