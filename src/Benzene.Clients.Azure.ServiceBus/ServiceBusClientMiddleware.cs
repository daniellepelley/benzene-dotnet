using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Azure.ServiceBus;

/// <summary>
/// Middleware that sends the <see cref="ServiceBusSendMessageContext"/>'s message via a
/// <see cref="ServiceBusSender"/> and records that the send completed.
/// </summary>
public class ServiceBusClientMiddleware : IMiddleware<ServiceBusSendMessageContext>, ITerminalMiddleware
{
    private readonly ServiceBusSender _sender;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceBusClientMiddleware"/> class.
    /// </summary>
    /// <param name="sender">The Service Bus sender (bound to a queue or topic) used to send the message.</param>
    /// <param name="cancellation">
    /// Supplies the ambient cancellation token to pass into the send call (the
    /// <c>HttpBenzeneMessageClient</c> constructor-optional accessor idiom); null observes no
    /// cancellation. Resolved automatically from the container on the DI-registered
    /// <c>UseServiceBusClient()</c> path; the explicit-client <c>UseServiceBusClient(sender)</c>
    /// overload resolves it from the pipeline's service resolver and passes it through.
    /// </param>
    public ServiceBusClientMiddleware(ServiceBusSender sender, ICancellationTokenAccessor? cancellation = null)
    {
        _sender = sender;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Gets the name of this middleware.
    /// </summary>
    public string Name => nameof(ServiceBusClientMiddleware);

    /// <summary>
    /// Sends the context's message via Service Bus. This is a terminal middleware; it does not call
    /// <paramref name="next"/>. Service Bus returns no payload, so success is a completed send.
    /// </summary>
    /// <param name="context">The context carrying the message to send.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(ServiceBusSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;
        await _sender.SendMessageAsync(context.Message, cancellationToken);
        context.IsSent = true;
    }
}
