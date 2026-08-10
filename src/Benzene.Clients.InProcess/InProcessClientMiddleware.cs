using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Middleware that dispatches the <see cref="InProcessSendMessageContext"/>'s request straight to the
/// in-process message dispatcher registered by <c>AddInProcessMessaging</c>, and records the response
/// on the context.
/// </summary>
public class InProcessClientMiddleware : IMiddleware<InProcessSendMessageContext>, ITerminalMiddleware
{
    private readonly IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse> _dispatcher;
    private readonly IServiceResolverFactory _serviceResolverFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessClientMiddleware"/> class.
    /// </summary>
    /// <param name="dispatcher">The in-process message dispatcher, registered by <c>AddInProcessMessaging</c>.</param>
    /// <param name="serviceResolverFactory">
    /// Used to give the dispatched handler its own fresh DI scope - the same isolation a message sent
    /// over a real wire transport would get in the receiving process, not the sending call's scope.
    /// </param>
    public InProcessClientMiddleware(IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse> dispatcher, IServiceResolverFactory serviceResolverFactory)
    {
        _dispatcher = dispatcher;
        _serviceResolverFactory = serviceResolverFactory;
    }

    /// <summary>
    /// Gets the name of this middleware.
    /// </summary>
    public string Name => nameof(InProcessClientMiddleware);

    /// <summary>
    /// Dispatches the context's request to the in-process handler and sets the response. This is a
    /// terminal middleware; it does not call <paramref name="next"/>.
    /// </summary>
    /// <param name="context">The context carrying the request to dispatch and to receive the response.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(InProcessSendMessageContext context, Func<Task> next)
    {
        context.Response = await _dispatcher.HandleAsync(context.Request, _serviceResolverFactory);
    }
}
