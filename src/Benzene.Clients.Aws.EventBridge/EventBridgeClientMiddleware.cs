using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.EventBridge;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Aws.EventBridge;

/// <summary>
/// Terminal send-pipeline middleware that publishes the built <c>PutEvents</c> request via the
/// injected EventBridge client. Failures propagate — callers (e.g.
/// <see cref="EventBridgeBenzeneMessageClient"/>) map them to a Benzene result.
/// </summary>
public class EventBridgeClientMiddleware : IMiddleware<EventBridgeSendMessageContext>, ITerminalMiddleware
{
    private readonly IAmazonEventBridge _amazonEventBridge;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventBridgeClientMiddleware"/> class with no
    /// cancellation-token accessor.
    /// </summary>
    public EventBridgeClientMiddleware(IAmazonEventBridge amazonEventBridge)
        : this(amazonEventBridge, null)
    {
    }

    /// <summary>
    /// Initializes the middleware, additionally resolving the ambient cancellation token so an
    /// upstream cancel/timeout aborts the outbound publish instead of running it to completion.
    /// </summary>
    public EventBridgeClientMiddleware(IAmazonEventBridge amazonEventBridge, ICancellationTokenAccessor? cancellation)
    {
        _amazonEventBridge = amazonEventBridge;
        _cancellation = cancellation;
    }

    public string Name => nameof(EventBridgeClientMiddleware);

    public async Task HandleAsync(EventBridgeSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;
        context.Response = await _amazonEventBridge.PutEventsAsync(context.Request, cancellationToken);
    }
}
