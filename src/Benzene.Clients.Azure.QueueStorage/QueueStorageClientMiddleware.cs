using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Azure.QueueStorage;

/// <summary>
/// Middleware that sends the <see cref="QueueStorageSendMessageContext"/>'s text via a
/// <see cref="QueueClient"/> and records that the send completed.
/// </summary>
public class QueueStorageClientMiddleware : IMiddleware<QueueStorageSendMessageContext>, ITerminalMiddleware
{
    private readonly QueueClient _queueClient;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueStorageClientMiddleware"/> class.
    /// </summary>
    /// <param name="queueClient">The queue client used to send the message.</param>
    /// <param name="cancellation">
    /// Supplies the ambient cancellation token to pass into the send call (the
    /// <c>HttpBenzeneMessageClient</c> constructor-optional accessor idiom); null observes no
    /// cancellation. Resolved automatically from the container on the DI-registered
    /// <c>UseQueueStorageClient()</c> path; the explicit-client <c>UseQueueStorageClient(queueClient)</c>
    /// overload resolves it from the pipeline's service resolver and passes it through.
    /// </param>
    public QueueStorageClientMiddleware(QueueClient queueClient, ICancellationTokenAccessor? cancellation = null)
    {
        _queueClient = queueClient;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Gets the name of this middleware.
    /// </summary>
    public string Name => nameof(QueueStorageClientMiddleware);

    /// <summary>
    /// Sends the context's message text to the queue. This is a terminal middleware; it does not call
    /// <paramref name="next"/>.
    /// </summary>
    /// <param name="context">The context carrying the message text to send.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(QueueStorageSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;
        await _queueClient.SendMessageAsync(context.MessageText, cancellationToken);
        context.IsSent = true;
    }
}
