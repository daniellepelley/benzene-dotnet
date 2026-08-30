using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Grpc;
using Benzene.Grpc.Test.Protos;
using Benzene.Results;

namespace Benzene.Grpc.Test.Handlers;

/// <summary>
/// Same shape as <see cref="ChatMessageHandler"/>, but throws partway through echoing messages -
/// the duplex-streaming counterpart of <see cref="SubscribeThrowingMidStreamMessageHandler"/> for #280.
/// </summary>
[GrpcMethod("/benzene.test.TestService/Chat")]
[Message("grpc-test-chat-throwing-topic")]
public class ChatThrowingMidStreamMessageHandler : IMessageHandler<IAsyncEnumerable<ChatMessage>, IAsyncEnumerable<ChatMessage>>
{
    public Task<IBenzeneResult<IAsyncEnumerable<ChatMessage>>> HandleAsync(IAsyncEnumerable<ChatMessage> request)
    {
        return Task.FromResult(BenzeneResult.Ok(Echo(request)));
    }

    private static async IAsyncEnumerable<ChatMessage> Echo(IAsyncEnumerable<ChatMessage> source)
    {
        var first = true;
        await foreach (var message in source)
        {
            if (!first)
            {
                throw new InvalidOperationException("boom mid-stream");
            }

            first = false;
            yield return new ChatMessage { Text = $"Echo: {message.Text}" };
        }
    }
}
