using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core;

namespace Benzene.Core.Middleware;

/// <summary>
/// Provides a middleware application that processes multiple contexts from a single event in parallel,
/// returning an array of results.
/// </summary>
/// <typeparam name="TEvent">The type of event to process.</typeparam>
/// <typeparam name="TContext">The type of context created from the event.</typeparam>
/// <typeparam name="TResult">The type of result returned for each context.</typeparam>
/// <remarks>
/// This application enables batch processing scenarios where a single event produces multiple contexts
/// that can be processed concurrently through the same pipeline. Each context is processed in its own
/// service scope, ensuring isolation between concurrent executions. <paramref name="maxDegreeOfParallelism"/>
/// optionally caps how many contexts run at once; <c>null</c> (the default) leaves the fan-out
/// unbounded, so every context starts immediately - the original behavior.
/// </remarks>
public class MiddlewareMultiApplication<TEvent, TContext, TResult>(
    IMiddlewarePipeline<TContext> pipelineBuilder,
    Func<TEvent, TContext[]> mapper,
    Func<TContext, TResult> resultMapper,
    int? maxDegreeOfParallelism = null)
    : IMiddlewareApplication<TEvent, TResult[]>
{
    /// <summary>
    /// Handles the event by mapping it to multiple contexts, processing them in parallel, and returning all results.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory for dependency resolution.</param>
    /// <returns>A task that represents the asynchronous operation, containing an array of processing results.</returns>
    public Task<TResult[]> HandleAsync(TEvent @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Handles the event by mapping it to multiple contexts, processing them in parallel, and
    /// returning all results, additionally seeding <b>each</b> per-record scope's ambient
    /// cancellation token so any component resolved during any of the batch's pipeline runs can
    /// observe cancellation via <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory for dependency resolution.</param>
    /// <param name="cancellationToken">
    /// The transport's cancellation token for this batch, or <see cref="CancellationToken.None"/> if
    /// it has no signal. The same token is seeded into every record's own scope - the spec's scope
    /// rule is one invocation per message even within a batch, so there is one token to seed per
    /// scope, not a token shared across contexts.
    /// </param>
    /// <returns>A task that represents the asynchronous operation, containing an array of processing results.</returns>
    public Task<TResult[]> HandleAsync(TEvent @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        return BoundedFanOut.WhenAllAsync(mapper(@event), async context =>
            {
                using var scope = serviceResolverFactory.CreateScope();
                scope.SeedCancellationToken(cancellationToken);
                await pipelineBuilder.HandleAsync(context, scope);
                return resultMapper(context);
            }, maxDegreeOfParallelism, cancellationToken);
    }
}

/// <summary>
/// Provides a middleware application that processes multiple contexts from a single event in parallel,
/// without returning results.
/// </summary>
/// <typeparam name="TEvent">The type of event to process.</typeparam>
/// <typeparam name="TContext">The type of context created from the event.</typeparam>
/// <remarks>
/// This application enables batch processing scenarios where a single event produces multiple contexts
/// that can be processed concurrently through the same pipeline. Each context is processed in its own
/// service scope, ensuring isolation between concurrent executions. <paramref name="maxDegreeOfParallelism"/>
/// optionally caps how many contexts run at once; <c>null</c> (the default) leaves the fan-out
/// unbounded, so every context starts immediately - the original behavior.
/// </remarks>
public class MiddlewareMultiApplication<TEvent, TContext>(
    IMiddlewarePipeline<TContext> pipeline,
    Func<TEvent, TContext[]> mapper,
    int? maxDegreeOfParallelism = null)
    : IMiddlewareApplication<TEvent>
{
    /// <summary>
    /// Handles the event by mapping it to multiple contexts and processing them in parallel.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory for dependency resolution.</param>
    /// <returns>A task representing the asynchronous operation that completes when all contexts are processed.</returns>
    public Task HandleAsync(TEvent @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Handles the event by mapping it to multiple contexts and processing them in parallel,
    /// additionally seeding <b>each</b> per-record scope's ambient cancellation token so any
    /// component resolved during any of the batch's pipeline runs can observe cancellation via
    /// <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    /// <param name="event">The event to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory for dependency resolution.</param>
    /// <param name="cancellationToken">
    /// The transport's cancellation token for this batch, or <see cref="CancellationToken.None"/> if
    /// it has no signal. The same token is seeded into every record's own scope - the spec's scope
    /// rule is one invocation per message even within a batch, so there is one token to seed per
    /// scope, not a token shared across contexts.
    /// </param>
    /// <returns>A task representing the asynchronous operation that completes when all contexts are processed.</returns>
    public Task HandleAsync(TEvent @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        return BoundedFanOut.WhenAllAsync(mapper(@event), async context =>
            {
                using var scope = serviceResolverFactory.CreateScope();
                scope.SeedCancellationToken(cancellationToken);
                await pipeline.HandleAsync(context, scope);
            }, maxDegreeOfParallelism, cancellationToken);
    }
}


