using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages;
using Benzene.Core.Messages;

namespace Benzene.Grpc;

/// <summary>Reads the inbound topic from a <see cref="GrpcContext"/>.</summary>
public class GrpcMessageTopicGetter : IMessageTopicGetter<GrpcContext>
{
    /// <inheritdoc />
    public ITopic GetTopic(GrpcContext context)
    {
        return new Topic(context.Topic);
    }
}
