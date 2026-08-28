using System.Threading;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Core.Middleware;

/// <summary>
/// Provides an abstract base class for middleware that routes requests to different handlers based on request properties.
/// </summary>
/// <typeparam name="TRequest">The type of request extracted from the context.</typeparam>
/// <typeparam name="TContext">The context type that the middleware operates on.</typeparam>
/// <remarks>
/// This abstract middleware enables routing patterns where different types or categories of requests
/// within the same context can be dispatched to different handlers. Derived classes implement the
/// extraction, routing logic, and handling behavior.
/// </remarks>
public abstract class MiddlewareRouter<TRequest, TContext>(IServiceResolver serviceResolver) : IMiddleware<TContext>, ITerminalMiddleware
{
    /// <summary>
    /// Gets the name of this middleware component. Defaults to the concrete router's own type name
    /// (via <see cref="object.GetType"/>) rather than a fixed <c>"MiddlewareRouter"</c>, so tracing
    /// shows which flavour of router ran (e.g. <c>SqsLambdaHandler</c>, <c>ApiGatewayLambdaHandler</c>)
    /// instead of the same generic label for every one. Override to supply a custom name.
    /// </summary>
    public virtual string Name => GetType().Name;

    /// <summary>
    /// Handles the middleware execution by extracting the request, checking if it can be handled,
    /// and either routing it or passing control to the next middleware.
    /// </summary>
    /// <param name="context">The current context being processed.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public virtual async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = TryExtractRequest(context);

        if (request == null)
        {
            await next();
        }
        else
        {
            if (CanHandle(request))
            {
                // #225: forward this scope's ambient cancellation token into the nested dispatch, so a
                // router-routed pipeline (e.g. Azure Event Hub/Queue Storage envelope routing) observes
                // the same host cancellation signal the outer scope does, instead of always running with
                // CancellationToken.None internally. TryGetService, not GetService: a caller that never
                // seeded/registered an accessor (e.g. NullServiceResolver, or a resolver built without
                // Benzene.Core's DI registrations) must keep working exactly as before.
                var cancellationToken = serviceResolver.TryGetService<ICancellationTokenAccessor>()?.CancellationToken
                    ?? CancellationToken.None;
                await HandleFunction(request, context, serviceResolver.GetService<IServiceResolverFactory>(), cancellationToken);
            }
            else
            {
                await next();
            }
        }
    }

    /// <summary>
    /// Determines whether this router can handle the given request.
    /// </summary>
    /// <param name="request">The request to evaluate.</param>
    /// <returns>True if this router can handle the request; otherwise, false.</returns>
    protected abstract bool CanHandle(TRequest request);

    /// <summary>
    /// Handles the request by executing the appropriate handler.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="context">The current context being processed.</param>
    /// <param name="serviceResolverFactory">The service resolver factory for dependency resolution.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task HandleFunction(TRequest request, TContext context, IServiceResolverFactory serviceResolverFactory);

    /// <summary>
    /// Handles the request by executing the appropriate handler, with the ambient cancellation token
    /// for this scope (#225). The default implementation delegates to the token-less
    /// <see cref="HandleFunction(TRequest,TContext,IServiceResolverFactory)"/> overload above, ignoring
    /// the token - so an existing (including third-party) subclass that only ever implemented the
    /// required abstract 3-arg overload keeps compiling and behaves byte-for-byte as before, with no
    /// override required. A subclass whose nested dispatch should observe cancellation (e.g. one that
    /// forwards to <c>MiddlewareApplication.HandleAsync(request, factory, token)</c>'s 3-arg,
    /// token-accepting overload) overrides this instead.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="context">The current context being processed.</param>
    /// <param name="serviceResolverFactory">The service resolver factory for dependency resolution.</param>
    /// <param name="cancellationToken">
    /// The ambient cancellation token for the current scope (see <see cref="ICancellationTokenAccessor"/>),
    /// or <see cref="CancellationToken.None"/> if none has been seeded.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected virtual Task HandleFunction(TRequest request, TContext context, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        => HandleFunction(request, context, serviceResolverFactory);

    /// <summary>
    /// Attempts to extract a request from the context.
    /// </summary>
    /// <param name="context">The context to extract the request from.</param>
    /// <returns>The extracted request, or null if no request could be extracted.</returns>
    protected abstract TRequest TryExtractRequest(TContext context);
}
