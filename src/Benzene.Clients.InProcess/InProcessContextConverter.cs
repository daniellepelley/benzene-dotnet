using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Converts between an outbound <see cref="OutboundContext"/> and an <see cref="InProcessSendMessageContext"/>,
/// so an outbound route (<c>OutboundRoutingBuilder.Route</c>) can dispatch straight to an in-process
/// handler registered via <c>AddInProcessMessaging</c>, without going over any wire transport. The
/// <see cref="OutboundContext"/> counterpart of the same pattern every other transport follows (see
/// <c>work/benzene-clients-redesign-plan.md</c> §3).
/// </summary>
/// <remarks>
/// Response mapping is deliberately untyped: the dispatched handler's <see cref="IBenzeneMessageResponse"/>
/// is set on <see cref="OutboundContext.Response"/> as a raw <see cref="BenzeneMessageClientResponse"/>,
/// the same envelope every message-client transport hands back. <see cref="DefaultBenzeneMessageSender"/>
/// deserializes it into the caller's requested <c>TResponse</c> once it knows that type.
/// </remarks>
public class InProcessContextConverter : IContextConverter<OutboundContext, InProcessSendMessageContext>
{
    private readonly ISerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessContextConverter"/> class using a
    /// <see cref="JsonSerializer"/> to serialize the outgoing message.
    /// </summary>
    public InProcessContextConverter()
        : this(new JsonSerializer())
    { }

    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessContextConverter"/> class.
    /// </summary>
    /// <param name="serializer">The serializer used to serialize the outgoing message.</param>
    public InProcessContextConverter(ISerializer serializer)
    {
        _serializer = serializer;
    }

    /// <summary>
    /// Builds an in-process message request, serializing the outgoing message as the body.
    /// </summary>
    /// <param name="contextIn">The outbound context to convert.</param>
    /// <returns>A task that resolves to the built <see cref="InProcessSendMessageContext"/>.</returns>
    public Task<InProcessSendMessageContext> CreateRequestAsync(OutboundContext contextIn) =>
        Task.FromResult(new InProcessSendMessageContext(InProcessRequestBuilder.Build(contextIn, _serializer)));

    /// <summary>
    /// Maps the dispatched handler's response back onto the outbound context as a raw
    /// <see cref="BenzeneMessageClientResponse"/>.
    /// </summary>
    /// <param name="contextIn">The outbound context to set the response on.</param>
    /// <param name="contextOut">The completed <see cref="InProcessSendMessageContext"/>.</param>
    /// <returns>A completed task.</returns>
    public Task MapResponseAsync(OutboundContext contextIn, InProcessSendMessageContext contextOut)
    {
        contextIn.Response = new BenzeneMessageClientResponse(contextOut.Response.StatusCode, contextOut.Response.Body, contextOut.Response.Headers);
        return Task.CompletedTask;
    }
}
