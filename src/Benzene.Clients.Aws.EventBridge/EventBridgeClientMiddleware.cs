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
    /// Initializes a new instance of the <see cref="EventBridgeClientMiddleware"/> class.
    /// </summary>
    /// <param name="amazonEventBridge">The EventBridge client used to put events.</param>
    /// <param name="cancellation">
    /// Supplies the ambient cancellation token to pass into the put-events call (the
    /// <c>HttpBenzeneMessageClient</c> constructor-optional accessor idiom); null observes no
    /// cancellation. Resolved automatically from the container on the DI-registered
    /// <c>UseEventBridgeClient()</c> path; the explicit-client <c>UseEventBridgeClient(amazonEventBridge)</c>
    /// overload resolves it from the pipeline's service resolver and passes it through.
    /// </param>
    public EventBridgeClientMiddleware(IAmazonEventBridge amazonEventBridge, ICancellationTokenAccessor? cancellation = null)
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
