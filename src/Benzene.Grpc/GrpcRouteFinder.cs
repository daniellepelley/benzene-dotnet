namespace Benzene.Grpc;

/// <summary>Default <see cref="IGrpcRouteFinder"/> implementation: an in-memory index built once from <see cref="IGrpcMethodFinder"/>.</summary>
public class GrpcRouteFinder : IGrpcRouteFinder
{
    private readonly IDictionary<string, IGrpcMethodDefinition> _grpcMethodDefinitionsByMethod;

    /// <summary>Initializes a new instance of the <see cref="GrpcRouteFinder"/> class.</summary>
    /// <param name="grpcMethodFinder">Discovers the routed method definitions to index.</param>
    public GrpcRouteFinder(IGrpcMethodFinder grpcMethodFinder)
    {
        _grpcMethodDefinitionsByMethod = grpcMethodFinder.FindDefinitions()
            .ToDictionary(x => x.Method, x => x, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IGrpcMethodDefinition? Find(string method)
    {
        return _grpcMethodDefinitionsByMethod.TryGetValue(method, out var definition) ? definition : null;
    }
}

