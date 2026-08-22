namespace Benzene.Grpc;

/// <summary>Default <see cref="IGrpcMethodDefinition"/> implementation.</summary>
public class GrpcMethodDefinition : IGrpcMethodDefinition
{
    /// <summary>Initializes a new instance of the <see cref="GrpcMethodDefinition"/> class.</summary>
    /// <param name="method">The gRPC method path.</param>
    /// <param name="topic">The Benzene topic that serves this method.</param>
    public GrpcMethodDefinition(string method, string topic)
    {
        Method = method;
        Topic = topic;
    }

    /// <inheritdoc />
    public string Method { get; }

    /// <inheritdoc />
    public string Topic { get; }
}
