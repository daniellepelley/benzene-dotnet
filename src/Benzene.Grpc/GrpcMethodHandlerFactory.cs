using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Grpc;

/// <summary>Default <see cref="IGrpcMethodHandlerFactory"/> implementation, backed by the configured gRPC pipeline.</summary>
public class GrpcMethodHandlerFactory : IGrpcMethodHandlerFactory
{
    private readonly IBenzeneServiceContainer _services;
    private readonly IMiddlewarePipeline<GrpcContext> _middlewarePipeline;

    /// <summary>Initializes a new instance of the <see cref="GrpcMethodHandlerFactory"/> class.</summary>
    /// <param name="services">The service container each created handler's DI scope resolves from.</param>
    /// <param name="middlewarePipeline">The pipeline each created handler runs a call through.</param>
    public GrpcMethodHandlerFactory(IBenzeneServiceContainer services, IMiddlewarePipeline<GrpcContext> middlewarePipeline)
    {
        _services = services;
        _middlewarePipeline = middlewarePipeline;
    }

    /// <inheritdoc />
    public IGrpcMethodHandler Create(IGrpcMethodDefinition grpcMethodDefinition)
    {
        return new GrpcMethodHandler(grpcMethodDefinition, _services.CreateServiceResolverFactory(), _middlewarePipeline);
    }
}
