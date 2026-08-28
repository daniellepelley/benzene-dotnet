using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs.Producer;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Azure.EventHub;

/// <summary>
/// Middleware that sends the <see cref="EventHubSendMessageContext"/>'s event via an
/// <see cref="EventHubProducerClient"/> and records that the send completed.
/// </summary>
public class EventHubClientMiddleware : IMiddleware<EventHubSendMessageContext>, ITerminalMiddleware
{
    private readonly EventHubProducerClient _producerClient;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventHubClientMiddleware"/> class.
    /// </summary>
    /// <param name="producerClient">The Event Hubs producer client used to send the event.</param>
    /// <param name="cancellation">
    /// Supplies the ambient cancellation token to pass into the batch-create/send calls (the
    /// <c>HttpBenzeneMessageClient</c> constructor-optional accessor idiom); null observes no
    /// cancellation. Resolved automatically from the container on the DI-registered
    /// <c>UseEventHubClient()</c> path; the explicit-client <c>UseEventHubClient(producerClient)</c>
    /// overload resolves it from the pipeline's service resolver and passes it through.
    /// </param>
    public EventHubClientMiddleware(EventHubProducerClient producerClient, ICancellationTokenAccessor? cancellation = null)
    {
        _producerClient = producerClient;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Gets the name of this middleware.
    /// </summary>
    public string Name => nameof(EventHubClientMiddleware);

    /// <summary>
    /// Sends the context's event via Event Hubs, as a single-event batch. This is a terminal middleware;
    /// it does not call <paramref name="next"/>. The producer client returns no payload, so success is a
    /// completed send.
    /// </summary>
    /// <param name="context">The context carrying the event to send.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(EventHubSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;

        // A partition key co-locates related events on one partition (preserving their order); without
        // it Event Hubs round-robins across partitions. The batch's key must be set at creation time.
        var batchOptions = string.IsNullOrEmpty(context.PartitionKey)
            ? new CreateBatchOptions()
            : new CreateBatchOptions { PartitionKey = context.PartitionKey };

        using var batch = await _producerClient.CreateBatchAsync(batchOptions, cancellationToken);
        if (!batch.TryAdd(context.EventData))
        {
            throw new InvalidOperationException("The event is too large to fit in a single Event Hubs batch.");
        }

        await _producerClient.SendAsync(batch, cancellationToken);
        context.IsSent = true;
    }
}
