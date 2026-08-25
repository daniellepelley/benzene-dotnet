using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Grpc;
using Benzene.Grpc.Test.Protos;
using Benzene.Results;

namespace Benzene.Grpc.Test.Handlers;

/// <summary>
/// A client-streaming fire-and-forget handler: consumes the request stream and succeeds with no
/// response payload (<see cref="BenzeneResult.Accepted{T}()"/> leaves <c>Payload</c> at its default,
/// <c>null</c> for the reference-type <see cref="UploadSummary"/>). Regression coverage for WP-4
/// (<c>ConvertResponse</c> null-payload handling).
/// </summary>
[GrpcMethod("/benzene.test.TestService/UploadFireAndForget")]
[Message("grpc-test-upload-fire-and-forget-topic")]
public class UploadFireAndForgetMessageHandler : IMessageHandler<IAsyncEnumerable<UploadItem>, UploadSummary>
{
    public async Task<IBenzeneResult<UploadSummary>> HandleAsync(IAsyncEnumerable<UploadItem> request)
    {
        await foreach (var _ in request)
        {
            // Drain the stream; the point of this handler is that it produces no response payload.
        }

        return BenzeneResult.Accepted<UploadSummary>();
    }
}
