using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Outbox;

/// <summary>
/// Registration extensions for the outbox: a pipeline extension to add the capture middleware to an
/// outbound route, and DI extensions to register the dispatch engine, an in-memory store, and the
/// poll-loop worker.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds outbox capture to an outbound route. Place it after cross-cutting stamping middleware
    /// (<c>UseW3CTraceContext()</c>/<c>UseCorrelationId()</c>) and before the terminal transport
    /// converter (<c>UseSqs(...)</c> etc.) - see <see cref="OutboxMiddleware"/>'s remarks. Requires
    /// <see cref="AddOutbox"/> (for <see cref="IOutboxStage"/>/<see cref="OutboxDispatchScope"/>) and
    /// an <see cref="IOutboxStore"/> (<see cref="AddInMemoryOutboxStore"/> or your own) to be
    /// registered.
    /// </summary>
    /// <param name="app">The outbound route's pipeline builder.</param>
    /// <param name="configure">
    /// Optionally overrides this route's <see cref="OutboxOptions"/> - only
    /// <see cref="OutboxOptions.WriteMode"/> and <see cref="OutboxOptions.StampIdempotencyKey"/> are
    /// consulted here. Starts from a clone of the shared instance <see cref="AddOutbox"/> registered
    /// (see the class remarks on <see cref="OutboxOptions"/>), so omitting <paramref name="configure"/>
    /// inherits whatever <see cref="AddOutbox"/> configured, and supplying it overrides just this
    /// route without affecting any other.
    /// </param>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseOutbox(
        this IMiddlewarePipelineBuilder<OutboundContext> app,
        Action<OutboxOptions>? configure = null)
    {
        return app.Use(resolver =>
        {
            var options = resolver.GetService<OutboxOptions>().Clone();
            configure?.Invoke(options);

            return new OutboxMiddleware(
                resolver.GetService<IOutboxStore>(),
                resolver.GetService<IOutboxStage>(),
                resolver.GetService<OutboxDispatchScope>(),
                resolver.GetService<ISerializer>(),
                options);
        });
    }

    /// <summary>
    /// Registers the outbox's process-wide pieces: the shared <see cref="OutboxOptions"/> the dispatch
    /// engine reads, the scoped <see cref="IOutboxStage"/>/<see cref="OutboxDispatchScope"/>, and
    /// <see cref="IOutboxDispatcher"/>. Does NOT register an <see cref="IOutboxStore"/> - call
    /// <see cref="AddInMemoryOutboxStore"/> (or register your own) as well.
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="configure">
    /// Configures the dispatch engine's shared <see cref="OutboxOptions"/> (<see cref="OutboxOptions.MaxAttempts"/>,
    /// <see cref="OutboxOptions.BackoffBase"/>, <see cref="OutboxOptions.BackoffCap"/>,
    /// <see cref="OutboxOptions.RetentionPeriod"/>, <see cref="OutboxOptions.BatchSize"/>,
    /// <see cref="OutboxOptions.ClaimLease"/>, <see cref="OutboxOptions.PollInterval"/>) - see the
    /// class remarks on <see cref="OutboxOptions"/> for why this is independent of each route's own
    /// <c>UseOutbox(configure)</c> options.
    /// </param>
    public static IBenzeneServiceContainer AddOutbox(this IBenzeneServiceContainer services, Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);

        services.TryAddScoped(resolver => new BufferedOutboxStage(
            resolver.TryGetService<ILoggerFactory>()?.CreateLogger("Benzene.Outbox.Stage") ?? NullLogger.Instance));
        services.TryAddScoped<IOutboxStage>(resolver => resolver.GetService<BufferedOutboxStage>());
        services.TryAddScoped<OutboxDispatchScope>();

        services.TryAddSingleton<IOutboxDispatcher>(resolver => new OutboxDispatcher(
            resolver.GetService<IOutboxStore>(),
            resolver.GetService<IServiceResolverFactory>(),
            options,
            resolver.TryGetService<ILoggerFactory>()?.CreateLogger("Benzene.Outbox.Dispatcher") ?? NullLogger.Instance));

        return services;
    }

    /// <summary>
    /// Registers <see cref="InMemoryOutboxStore"/> as the <see cref="IOutboxStore"/>. Suitable for a
    /// single worker instance, tests, and local development; use a shared store (e.g.
    /// <c>Benzene.Outbox.DynamoDb</c>) in a multi-instance deployment or when
    /// <see cref="OutboxWriteMode.Transactional"/> atomic commits are needed. Call once at
    /// application setup.
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="now">A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static IBenzeneServiceContainer AddInMemoryOutboxStore(this IBenzeneServiceContainer services, Func<DateTimeOffset>? now = null)
    {
        services.TryAddSingleton<IOutboxStore>(new InMemoryOutboxStore(now));
        return services;
    }

    /// <summary>
    /// Registers <see cref="OutboxDispatcherWorker"/> so it is resolvable both as itself and as
    /// <see cref="IBenzeneWorker"/>, for whichever host you wire it into
    /// (<c>Benzene.HostedService</c>'s generic-host adapter, or a <c>Benzene.SelfHost</c> worker
    /// startup's <c>Workers.Add(...)</c>). Requires <see cref="AddOutbox"/> and an
    /// <see cref="IOutboxStore"/> to already be registered.
    /// </summary>
    /// <param name="services">The service container.</param>
    public static IBenzeneServiceContainer AddOutboxDispatcherWorker(this IBenzeneServiceContainer services)
    {
        services.TryAddSingleton(resolver => new OutboxDispatcherWorker(
            resolver.GetService<IOutboxDispatcher>(),
            resolver.GetService<OutboxOptions>(),
            resolver.TryGetService<ILoggerFactory>()?.CreateLogger("Benzene.Outbox.DispatcherWorker") ?? NullLogger.Instance));
        services.TryAddSingleton<IBenzeneWorker>(resolver => resolver.GetService<OutboxDispatcherWorker>());

        return services;
    }
}
