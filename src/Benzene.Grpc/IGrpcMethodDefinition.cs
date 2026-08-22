namespace Benzene.Grpc;

/// <summary>Maps one gRPC method path to the Benzene topic that serves it.</summary>
public interface IGrpcMethodDefinition
{
    /// <summary>Gets the gRPC method path (e.g. <c>/package.Service/Method</c>).</summary>
    string Method { get; }

    /// <summary>Gets the Benzene topic that serves this method.</summary>
    string Topic { get; }
}