using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;

namespace Benzene.ClaimCheck;

/// <summary>
/// Registration extensions for the claim-check middleware pair: <see cref="UseClaimCheck"/> (offload)
/// for an outbound route, <see cref="UseClaimCheck{TContext}"/> (hydrate) for an inbound transport
/// pipeline, and <see cref="AddInMemoryClaimCheckStore"/> to register the in-process
/// <see cref="IClaimCheckStore"/>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds <see cref="ClaimCheckOffloadMiddleware"/> to an outbound <see cref="OutboundContext"/>
    /// route pipeline. Requires an <see cref="IClaimCheckStore"/> to be registered (see
    /// <see cref="AddInMemoryClaimCheckStore"/> or register your own).
    /// </summary>
    /// <param name="app">The outbound pipeline builder to add the middleware to.</param>
    /// <param name="configure">Optional configuration of <see cref="ClaimCheckOptions"/>.</param>
    /// <remarks>
    /// Add this as the last non-terminal step of the route, immediately before the transport
    /// converter (<c>UseSqs</c>/<c>UseSns</c>/etc.) - see <see cref="ClaimCheckOffloadMiddleware"/>'s
    /// remarks for the full placement reasoning.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseClaimCheck(
        this IMiddlewarePipelineBuilder<OutboundContext> app,
        Action<ClaimCheckOptions>? configure = null)
    {
        var options = new ClaimCheckOptions();
        configure?.Invoke(options);

        return app.Use(resolver => new ClaimCheckOffloadMiddleware(
            resolver.GetService<IClaimCheckStore>(),
            options));
    }

    /// <summary>
    /// Adds <see cref="ClaimCheckHydrateMiddleware{TContext}"/> to an inbound transport pipeline.
    /// Requires an <see cref="IClaimCheckStore"/> to be registered (see
    /// <see cref="AddInMemoryClaimCheckStore"/> or register your own), and an
    /// <see cref="IMessageHeadersGetter{TContext}"/> for <typeparamref name="TContext"/> (every
    /// transport's <c>UseMessageHandlers</c> registers one already). An
    /// <see cref="IMessageBodySetter{TContext}"/> is only required at the moment a message actually
    /// carries a claim-check reference to hydrate - see <see cref="ClaimCheckHydrateMiddleware{TContext}"/>'s
    /// remarks.
    /// </summary>
    /// <typeparam name="TContext">The transport-specific message context type.</typeparam>
    /// <param name="app">The pipeline builder to add the middleware to.</param>
    /// <param name="configure">Optional configuration of <see cref="ClaimCheckOptions"/>.</param>
    /// <remarks>
    /// Add this after the observability prelude (so a store fetch appears in the trace) and before
    /// <c>UseMessageHandlers</c> (the deserialization boundary).
    /// </remarks>
    public static IMiddlewarePipelineBuilder<TContext> UseClaimCheck<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app,
        Action<ClaimCheckOptions>? configure = null)
    {
        var options = new ClaimCheckOptions();
        configure?.Invoke(options);

        return app.Use(resolver => new ClaimCheckHydrateMiddleware<TContext>(
            resolver.GetService<IClaimCheckStore>(),
            resolver.GetService<IMessageHeadersGetter<TContext>>(),
            resolver.TryGetService<IMessageBodySetter<TContext>>(),
            options));
    }

    /// <summary>
    /// Registers <see cref="InMemoryClaimCheckStore"/> as the <see cref="IClaimCheckStore"/>.
    /// Suitable for a single worker instance, tests, and local development; use a shared/durable
    /// store (e.g. the S3 or Azure Blob Storage claim-check packages) in a multi-instance deployment,
    /// where an offload on one instance must be resolvable by any instance that receives the message.
    /// Call once at application setup.
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="timeToLive">How long offloaded payloads are retained. Defaults to 24 hours.</param>
    public static IBenzeneServiceContainer AddInMemoryClaimCheckStore(
        this IBenzeneServiceContainer services,
        TimeSpan? timeToLive = null)
    {
        services.AddSingleton<IClaimCheckStore>(new InMemoryClaimCheckStore(timeToLive));
        return services;
    }
}
