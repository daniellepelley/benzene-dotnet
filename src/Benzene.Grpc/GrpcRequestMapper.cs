using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Grpc.Serialization;
using Benzene.Grpc.Streaming;

namespace Benzene.Grpc;

/// <summary>Maps a <see cref="GrpcContext"/>'s request onto a handler's declared request type.</summary>
public class GrpcRequestMapper : IRequestMapper<GrpcContext>
{
    private readonly IGrpcMessageAdapter _adapter;

    /// <summary>Initializes a new instance of the <see cref="GrpcRequestMapper"/> class.</summary>
    /// <param name="adapter">Converts between the wire request/response and the handler's declared types.</param>
    public GrpcRequestMapper(IGrpcMessageAdapter adapter)
    {
        _adapter = adapter;
    }

    /// <inheritdoc />
    public TRequest? GetBody<TRequest>(GrpcContext context) where TRequest : class
    {
        if (context.RequestAsObject is TRequest direct)
        {
            return direct;
        }

        if (GrpcStreamAdapter.TryConvertStream(context.RequestAsObject, typeof(TRequest), _adapter, isResponseDirection: false, context.CancellationToken) is TRequest convertedStream)
        {
            return convertedStream;
        }

        return _adapter.ConvertRequest<TRequest>(context.RequestAsObject);
    }
}
