using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Provides the middleware pipeline context for dispatching a single message to an in-process
/// handler, in the shared <see cref="IBenzeneMessageRequest"/>/<see cref="IBenzeneMessageResponse"/>
/// envelope every transport uses - the in-process counterpart of <c>SqsSendMessageContext</c>.
/// </summary>
public class InProcessSendMessageContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessSendMessageContext"/> class.
    /// </summary>
    /// <param name="request">The in-process message request.</param>
    public InProcessSendMessageContext(IBenzeneMessageRequest request)
    {
        Request = request;
    }

    /// <summary>
    /// Gets the in-process message request.
    /// </summary>
    public IBenzeneMessageRequest Request { get; }

    /// <summary>
    /// Gets or sets the in-process message response. Set by <see cref="InProcessClientMiddleware"/>.
    /// </summary>
    public IBenzeneMessageResponse Response { get; set; } = null!;
}
