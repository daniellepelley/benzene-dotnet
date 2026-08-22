namespace Benzene.Grpc;

/// <summary>Discovers the routed gRPC method definitions across registered message handlers.</summary>
public interface IGrpcMethodFinder
{
    /// <summary>Finds every <c>[GrpcMethod]</c>-decorated handler's method definition.</summary>
    IGrpcMethodDefinition[] FindDefinitions();
}