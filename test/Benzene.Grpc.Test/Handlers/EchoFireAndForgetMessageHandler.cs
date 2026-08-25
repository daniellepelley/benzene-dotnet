using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Grpc;
using Benzene.Grpc.Test.Protos;
using Benzene.Results;

namespace Benzene.Grpc.Test.Handlers;

/// <summary>
/// A unary fire-and-forget handler: succeeds with no response payload (<see cref="BenzeneResult.Accepted{T}()"/>
/// leaves <c>Payload</c> at its default, <c>null</c> for the reference-type <see cref="EchoReply"/>).
/// Regression coverage for WP-4 (<c>ConvertResponse</c> null-payload handling).
/// </summary>
[GrpcMethod("/benzene.test.TestService/EchoFireAndForget")]
[Message("grpc-test-echo-fire-and-forget-topic")]
public class EchoFireAndForgetMessageHandler : IMessageHandler<EchoRequest, EchoReply>
{
    public Task<IBenzeneResult<EchoReply>> HandleAsync(EchoRequest request)
    {
        return Task.FromResult(BenzeneResult.Accepted<EchoReply>());
    }
}
