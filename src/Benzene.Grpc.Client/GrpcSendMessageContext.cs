using Grpc.Core;

namespace Benzene.Grpc.Client;

/// <summary>Middleware pipeline context for making a single outbound gRPC call.</summary>
public class GrpcSendMessageContext
{
    /// <summary>Initializes a new instance of the <see cref="GrpcSendMessageContext"/> class.</summary>
    /// <param name="topic">The Benzene topic being called, used to resolve the gRPC route.</param>
    /// <param name="message">The request message (POCO or protobuf).</param>
    /// <param name="headers">The call's outbound metadata.</param>
    /// <param name="deadline">The call's absolute deadline, if any.</param>
    /// <param name="cancellationToken">The token that aborts the call.</param>
    public GrpcSendMessageContext(string topic, object message, Metadata headers, DateTime? deadline, CancellationToken cancellationToken)
    {
        Topic = topic;
        Message = message;
        Headers = headers;
        Deadline = deadline;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the Benzene topic being called.</summary>
    public string Topic { get; }

    /// <summary>Gets the request message.</summary>
    public object Message { get; }

    /// <summary>Gets the call's outbound metadata.</summary>
    public Metadata Headers { get; }

    /// <summary>Gets the call's absolute deadline, if any.</summary>
    public DateTime? Deadline { get; }

    /// <summary>Gets the token that aborts the call.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets or sets the raw protobuf response, set once the call completes.</summary>
    public object? Response { get; set; }

    /// <summary>Gets or sets the call's resulting status.</summary>
    public Status Status { get; set; }

    /// <summary>Gets or sets the call's response trailers.</summary>
    public Metadata? ResponseTrailers { get; set; }
}
