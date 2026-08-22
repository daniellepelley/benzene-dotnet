namespace Benzene.Grpc;

/// <summary>
/// Decorates a message handler class with the gRPC method path it serves (e.g.
/// <c>/package.Service/Method</c>), combined with <c>[Message("topic")]</c>. Discovered by
/// <see cref="ReflectionGrpcMethodFinder"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class GrpcMethodAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="GrpcMethodAttribute"/> class.</summary>
    /// <param name="method">The gRPC method path this handler serves.</param>
    public GrpcMethodAttribute(string method)
    {
        Method = method;
    }

    /// <summary>Gets the gRPC method path this handler serves.</summary>
    public string Method { get; }
}