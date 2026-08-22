namespace Benzene.Grpc;

/// <summary>Default <see cref="IGrpcMethodHandlerFactoryAccessor"/> implementation: a plain mutable holder.</summary>
public class GrpcMethodHandlerFactoryAccessor : IGrpcMethodHandlerFactoryAccessor
{
    /// <inheritdoc />
    public IGrpcMethodHandlerFactory? Factory { get; set; }
}
