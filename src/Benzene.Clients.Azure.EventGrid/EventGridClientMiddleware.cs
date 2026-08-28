using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventGrid;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Azure.EventGrid;

/// <summary>
/// Middleware that sends the <see cref="EventGridSendMessageContext"/>'s event via an
/// <see cref="EventGridPublisherClient"/> and records that the send completed.
/// </summary>
public class EventGridClientMiddleware : IMiddleware<EventGridSendMessageContext>, ITerminalMiddleware
{
    private readonly EventGridPublisherClient _publisherClient;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventGridClientMiddleware"/> class.
    /// </summary>
    /// <param name="publisherClient">The Event Grid publisher client used to send the event.</param>
    /// <param name="cancellation">
    /// Supplies the ambient cancellation token to pass into the send call (the
    /// <c>HttpBenzeneMessageClient</c> constructor-optional accessor idiom); null observes no
    /// cancellation. Resolved automatically from the container on the DI-registered
    /// <c>UseEventGridClient()</c> path; the explicit-client <c>UseEventGridClient(publisherClient)</c>
    /// overload resolves it from the pipeline's service resolver and passes it through.
    /// </param>
    public EventGridClientMiddleware(EventGridPublisherClient publisherClient, ICancellationTokenAccessor? cancellation = null)
    {
        _publisherClient = publisherClient;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Gets the name of this middleware.
    /// </summary>
    public string Name => nameof(EventGridClientMiddleware);

    /// <summary>
    /// Sends the context's event via Event Grid, using whichever of <see cref="EventGridSendMessageContext.CloudEvent"/>
    /// / <see cref="EventGridSendMessageContext.EventGridEvent"/> is set. This is a terminal middleware; it
    /// does not call <paramref name="next"/>. The publisher client returns no payload, so success is a
    /// completed send.
    /// </summary>
    /// <param name="context">The context carrying the event to send.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(EventGridSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;

        if (context.CloudEvent != null)
        {
            await _publisherClient.SendEventAsync(context.CloudEvent, cancellationToken);
        }
        else
        {
            await _publisherClient.SendEventAsync(context.EventGridEvent!, cancellationToken);
        }

        context.IsSent = true;
    }
}
