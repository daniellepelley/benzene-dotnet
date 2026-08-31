using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Grpc;
using Benzene.Grpc.Test.Protos;
using Benzene.Results;

namespace Benzene.Grpc.Test.Handlers;

/// <summary>
/// Same shape as <see cref="SubscribeMessageHandler"/>, but throws partway through producing items -
/// used to prove/regress #280 (a mid-stream handler exception must be classified the same way a unary
/// handler's exception is, not surface as an unclassified <c>RpcException(Unknown)</c> with a stale
/// success trailer).
/// </summary>
[GrpcMethod("/benzene.test.TestService/Subscribe")]
[Message("grpc-test-subscribe-throwing-topic")]
public class SubscribeThrowingMidStreamMessageHandler : IMessageHandler<SubscribeRequest, IAsyncEnumerable<SubscribeReply>>
{
    public Task<IBenzeneResult<IAsyncEnumerable<SubscribeReply>>> HandleAsync(SubscribeRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(Produce(request.Topic)));
    }

    private static async IAsyncEnumerable<SubscribeReply> Produce(string topic)
    {
        yield return new SubscribeReply { Item = $"{topic}-0" };
        await Task.Yield();
        throw new InvalidOperationException("boom mid-stream");
    }
}
