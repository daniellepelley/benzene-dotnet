using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;

namespace Benzene.Grpc;

/// <summary>Records the handler's outcome (and response payload) onto a <see cref="GrpcContext"/>.</summary>
public class GrpcMessageHandlerResultSetter : IMessageHandlerResultSetter<GrpcContext>
{
    /// <inheritdoc />
    public Task SetResultAsync(GrpcContext context, IMessageHandlerResult messageHandlerResult)
    {
        context.MessageHandlerResult = messageHandlerResult;
        context.ResponseAsObject = messageHandlerResult.BenzeneResult.PayloadAsObject;
        return Task.CompletedTask;
    }
}