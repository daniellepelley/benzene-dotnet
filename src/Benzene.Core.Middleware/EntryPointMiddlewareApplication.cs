using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Core.Middleware;

/// <summary>
/// Provides an entry point wrapper for middleware applications that handles events without returning results.
/// </summary>
/// <typeparam name="TEvent">The type of event to process.</typeparam>
/// <remarks>
/// This class acts as an adapter that simplifies the invocation of middleware applications by
/// encapsulating the service resolver factory, providing a cleaner API for external callers.
/// </remarks>
public class EntryPointMiddlewareApplication<TEvent>(
    IMiddlewareApplication<TEvent> middlewareApplication,
    IServiceResolverFactory serviceResolverFactory)
    : IEntryPointMiddlewareApplication<TEvent>
{
    /// <summary>
    /// Sends an event to the middleware application for processing.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SendAsync(TEvent @event)
    {
        return middlewareApplication.HandleAsync(@event, serviceResolverFactory);
    }

    /// <summary>
    /// Sends an event to the middleware application for processing, forwarding
    /// <paramref name="cancellationToken"/> so it is seeded into the pipeline's per-event scope.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <param name="cancellationToken">
    /// The host's cancellation token for this event, or <see cref="CancellationToken.None"/> if the
    /// host has no cancellation signal.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SendAsync(TEvent @event, CancellationToken cancellationToken)
    {
        return middlewareApplication.HandleAsync(@event, serviceResolverFactory, cancellationToken);
    }
}

/// <summary>
/// Provides an entry point wrapper for middleware applications that handles events and returns results.
/// </summary>
/// <typeparam name="TEvent">The type of event to process.</typeparam>
/// <typeparam name="TResult">The type of result returned after processing.</typeparam>
/// <remarks>
/// This class acts as an adapter that simplifies the invocation of middleware applications by
/// encapsulating the service resolver factory, providing a cleaner API for external callers.
/// </remarks>
public class EntryPointMiddlewareApplication<TEvent, TResult>(
    IMiddlewareApplication<TEvent, TResult> middlewareApplication,
    IServiceResolverFactory serviceResolverFactory)
    : IEntryPointMiddlewareApplication<TEvent, TResult>
{
    /// <summary>
    /// Sends an event to the middleware application for processing and returns a result.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <returns>A task that represents the asynchronous operation, containing the processing result.</returns>
    public Task<TResult> SendAsync(TEvent @event)
    {
        return middlewareApplication.HandleAsync(@event, serviceResolverFactory);
    }

    /// <summary>
    /// Sends an event to the middleware application for processing and returns a result, forwarding
    /// <paramref name="cancellationToken"/> so it is seeded into the pipeline's per-event scope.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <param name="cancellationToken">
    /// The host's cancellation token for this event, or <see cref="CancellationToken.None"/> if the
    /// host has no cancellation signal.
    /// </param>
    /// <returns>A task that represents the asynchronous operation, containing the processing result.</returns>
    public Task<TResult> SendAsync(TEvent @event, CancellationToken cancellationToken)
    {
        return middlewareApplication.HandleAsync(@event, serviceResolverFactory, cancellationToken);
    }
}
