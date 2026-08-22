using Benzene.Abstractions.Messages.Mappers;
using Google.Protobuf;

namespace Benzene.Grpc;

/// <summary>Reads the inbound message body from a <see cref="GrpcContext"/>, JSON-formatting a protobuf request.</summary>
public class GrpcMessageBodyGetter : IMessageBodyGetter<GrpcContext>
{
    /// <inheritdoc />
    public string? GetBody(GrpcContext context)
    {
        return context.RequestAsObject is IMessage message
            ? JsonFormatter.Default.Format(message)
            : System.Text.Json.JsonSerializer.Serialize(context.RequestAsObject);
    }
}