using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Results;
using Grpc.Core;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Grpc.Client;

/// <summary>
/// Bridges a <see cref="Void"/>-response <see cref="IBenzeneClientContext{T,Void}"/> pipeline stage (see
/// <c>UseGrpc&lt;T&gt;</c>) onto <see cref="GrpcSendMessageContext"/>. <see cref="GrpcBenzeneMessageClient"/>
/// does not use this converter's <see cref="MapResponseAsync"/> - it inspects the real, typed gRPC response
/// itself - this class exists for embedding a gRPC send as one step of a broader client pipeline.
/// </summary>
public class GrpcContextConverter<T> : IContextConverter<IBenzeneClientContext<T, Void>, GrpcSendMessageContext>
{
    private readonly CancellationToken _cancellationToken;
    private readonly System.DateTime? _deadline;

    /// <summary>Creates the converter, optionally carrying a cancellation token and an absolute call
    /// deadline onto the outbound call so an upstream cancel/deadline aborts the downstream RPC and the
    /// downstream inherits the same wall-clock deadline (deadline propagation).</summary>
    public GrpcContextConverter(CancellationToken cancellationToken = default, System.DateTime? deadline = null)
    {
        _cancellationToken = cancellationToken;
        _deadline = deadline;
    }

    /// <summary>Builds the outbound gRPC send context, converting headers from the client context.</summary>
    /// <param name="contextIn">The client context to convert.</param>
    /// <returns>A task that resolves to the built <see cref="GrpcSendMessageContext"/>.</returns>
    public Task<GrpcSendMessageContext> CreateRequestAsync(IBenzeneClientContext<T, Void> contextIn)
    {
        var headers = new Metadata();
        foreach (var header in contextIn.Request.Headers)
        {
            headers.Add(header.Key, header.Value);
        }

        return Task.FromResult(new GrpcSendMessageContext(contextIn.Request.Topic, contextIn.Request.Message, headers, deadline: _deadline, _cancellationToken));
    }

    /// <summary>Maps an OK status to a successful result, or any other status to service-unavailable.</summary>
    /// <param name="contextIn">The client context to set the response on.</param>
    /// <param name="contextOut">The completed <see cref="GrpcSendMessageContext"/>.</param>
    public Task MapResponseAsync(IBenzeneClientContext<T, Void> contextIn, GrpcSendMessageContext contextOut)
    {
        contextIn.Response = contextOut.Status.StatusCode == StatusCode.OK
            ? BenzeneResult.Ok<Void>()
            : BenzeneResult.ServiceUnavailable<Void>(contextOut.Status.Detail);
        return Task.CompletedTask;
    }
}
