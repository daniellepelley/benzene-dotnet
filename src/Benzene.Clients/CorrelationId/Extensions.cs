using Benzene.Abstractions;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.CorrelationId;

/// <summary>
/// Provides <see cref="UseCorrelationId"/> for an outbound <see cref="OutboundContext"/> pipeline -
/// see <c>work/archive/benzene-clients-redesign-plan-2026-07.md</c>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds <see cref="CorrelationIdMiddleware"/> to an outbound route pipeline, so every send
    /// through it carries the current correlation ID.
    /// </summary>
    /// <param name="app">The outbound pipeline builder to add the middleware to.</param>
    /// <param name="correlationKey">
    /// The header key to stamp the correlation ID onto. Pass explicitly to override for this route
    /// only; leave <c>null</c> (the default) to use a DI-registered <see cref="CorrelationHeaderOptions"/>
    /// if one is registered, or <see cref="CorrelationHeaderDefaults.HeaderKey"/> otherwise - so a
    /// single <see cref="CorrelationHeaderOptions"/> registration changes the key everywhere without
    /// touching every <c>UseCorrelationId()</c> call.
    /// </param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseCorrelationId(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string? correlationKey = null)
    {
        return app.Use(resolver => new CorrelationIdMiddleware(
            resolver.GetService<ICorrelationId>(),
            correlationKey ?? resolver.TryGetService<CorrelationHeaderOptions>()?.HeaderKey ?? CorrelationHeaderDefaults.HeaderKey));
    }
}
