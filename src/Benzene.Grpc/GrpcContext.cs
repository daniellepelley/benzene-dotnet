using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.MessageHandlers.Request;
using Grpc.Core;

namespace Benzene.Grpc;

/// <summary>Message pipeline context for a single gRPC call, of any call shape (unary/streaming).</summary>
public class GrpcContext : IHasMessageResult
{
    /// <summary>Gets the Benzene topic this call was routed to.</summary>
    public string Topic { get; }

    /// <summary>Gets the underlying gRPC server call context.</summary>
    public ServerCallContext CallContext { get; }

    /// <summary>Gets the request, boxed as <see cref="object"/> (the untyped counterpart of <see cref="GrpcContext{TRequest,TResponse}.Request"/>).</summary>
    public virtual object RequestAsObject { get; }

    /// <summary>Gets or sets the response, boxed as <see cref="object"/> (the untyped counterpart of <see cref="GrpcContext{TRequest,TResponse}.Response"/>).</summary>
    public virtual object? ResponseAsObject { get; set; }

    /// <summary>Gets or sets an untyped response payload, for a handler that produced a response the pipeline must convert to <c>TResponse</c>.</summary>
    public object? ResponsePayload { get; set; }

    /// <summary>The token that's cancelled if the call is cancelled or its deadline is exceeded.</summary>
    public CancellationToken CancellationToken => CallContext.CancellationToken;

    /// <summary>
    /// Metadata to send back to the client before the first response message. Written by the transport
    /// once the pipeline completes; empty means no response headers are sent.
    /// </summary>
    public Metadata ResponseHeaders { get; } = new();

    /// <summary>Metadata to send back to the client after the response. Backed directly by the call.</summary>
    public Metadata ResponseTrailers => CallContext.ResponseTrailers;

    /// <summary>Initializes a new instance of the <see cref="GrpcContext"/> class.</summary>
    /// <param name="topic">The Benzene topic this call was routed to.</param>
    /// <param name="callContext">The underlying gRPC server call context.</param>
    public GrpcContext(string topic, ServerCallContext callContext)
    {
        Topic = topic;
        CallContext = callContext;
    }

    /// <inheritdoc />
    public IBenzeneResult MessageResult { get; set; }

    /// <summary>Gets or sets the message handler's result, once the pipeline has dispatched to one.</summary>
    public IMessageHandlerResult? MessageHandlerResult { get; set; }
}

/// <summary>The typed <see cref="GrpcContext"/> for a specific request/response pair.</summary>
public class GrpcContext<TRequest, TResponse> : GrpcContext, IRequestContext<TRequest>
{
    /// <summary>Initializes a new instance of the <see cref="GrpcContext{TRequest,TResponse}"/> class.</summary>
    /// <param name="topic">The Benzene topic this call was routed to.</param>
    /// <param name="callContext">The underlying gRPC server call context.</param>
    /// <param name="request">The typed request.</param>
    public GrpcContext(string topic, ServerCallContext callContext, TRequest request)
        : base(topic, callContext)
    {
        Request = request;
    }

    /// <inheritdoc />
    public override object RequestAsObject => Request;

    /// <inheritdoc />
    public override object? ResponseAsObject
    {
        get => (object?)Response ?? ResponsePayload;
        set
        {
            if (value is TResponse typed)
            {
                Response = typed;
                return;
            }

            ResponsePayload = value;
        }
    }

    /// <summary>Gets the typed request.</summary>
    public TRequest Request { get; }

    /// <summary>Gets or sets the typed response.</summary>
    public TResponse? Response { get; set; }
}
