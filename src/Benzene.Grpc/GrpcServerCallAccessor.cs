using Grpc.Core;

namespace Benzene.Grpc;

/// <summary>Default <see cref="IGrpcServerCallAccessor"/> implementation: a scoped, mutable holder populated per call.</summary>
public class GrpcServerCallAccessor : IGrpcServerCallAccessor
{
    /// <inheritdoc cref="IGrpcServerCallAccessor.CallContext" />
    public ServerCallContext? CallContext { get; set; }

    /// <inheritdoc />
    public CancellationToken CancellationToken => CallContext?.CancellationToken ?? CancellationToken.None;
}
