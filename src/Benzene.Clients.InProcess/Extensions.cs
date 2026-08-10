using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Provides extension methods for wiring <see cref="InProcessClientMiddleware"/> into outbound routing.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to dispatch straight
    /// to the named in-process pipeline registered via <c>AddInProcessMessaging(registry =>
    /// registry.Add(name, ...))</c>, without leaving the process.
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="name">
    /// The in-process pipeline's name, matching the name it was added under. Defaults to
    /// <see cref="InProcessMessagingBuilder.DefaultName"/> - the single-pipeline
    /// <c>AddInProcessMessaging(configure)</c> overload registers under that name.
    /// </param>
    /// <returns>The pipeline builder, for chaining.</returns>
    /// <remarks>
    /// Registers this route's <paramref name="name"/> (so <see cref="InProcessRouteStartUpCheck"/>
    /// can validate it was actually registered) and the check itself, idempotently - every
    /// <c>.UseInProcess(...)</c> call anywhere in the app contributes to the same check.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseInProcess(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string name = InProcessMessagingBuilder.DefaultName)
    {
        app.Register(services =>
        {
            services.AddSingleton(new InProcessRouteReference(name));
            services.TryAddSingletonImplementation<IStartUpCheck, InProcessRouteStartUpCheck>();
        });

        return app.Convert(new InProcessContextConverter(), builder => builder.Use(resolver =>
            new InProcessClientMiddleware(
                resolver.GetService<InProcessDispatcherRegistry>().Resolve(name),
                resolver.GetService<IServiceResolverFactory>())));
    }

    /// <summary>
    /// Converts an outbound route pipeline to dispatch to <em>every</em> target in
    /// <paramref name="targets"/> concurrently - the in-monolith equivalent of one SNS topic fanning
    /// out to several subscribers. Each target is a (pipeline, topic) pair, <b>not just a pipeline
    /// name</b>: Benzene's (topic, version) → at most one handler invariant is enforced
    /// process-wide, not per in-process pipeline (every named pipeline
    /// <see cref="InProcessMessagingBuilder"/> builds shares the same underlying handler
    /// registration), so two targets reacting to what is conceptually one event must each dispatch
    /// under a topic of their own - see <see cref="InProcessFanOutTarget"/> and
    /// <see cref="DuplicateInProcessFanOutTargetException"/>.
    /// </summary>
    /// <remarks>
    /// The route this terminates must be sent via
    /// <c>IBenzeneMessageSender.SendAsync&lt;TRequest,Void&gt;</c>; requesting any other
    /// <c>TResponse</c> throws <c>OutboundResponseTypeMismatchException</c>, the same runtime check
    /// that already applies to every other fire-and-forget-only transport (SQS, SNS) - see
    /// <c>work/inprocess-fanout-design.md</c> for why no separate compile-time or start-up mechanism
    /// was built for this: there is no existing precedent for one, and this follows the established
    /// pattern instead of inventing a new one. Also registers one
    /// <see cref="InProcessRouteReference"/> per target's pipeline name (so
    /// <see cref="InProcessRouteStartUpCheck"/> validates every one of them - no changes needed to
    /// the check itself) and the check itself, idempotently.
    /// </remarks>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="targets">Every pipeline/topic pair to fan out to. Must be non-empty, with no two targets sharing a topic.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="targets"/> is empty.</exception>
    /// <exception cref="DuplicateInProcessFanOutTargetException">Two targets name the same topic.</exception>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseInProcessFanOut(
        this IMiddlewarePipelineBuilder<OutboundContext> app, params InProcessFanOutTarget[] targets)
    {
        if (targets.Length == 0)
        {
            throw new ArgumentException("UseInProcessFanOut requires at least one target.", nameof(targets));
        }

        var duplicateTopic = targets
            .GroupBy(t => t.Topic, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateTopic != null)
        {
            throw new DuplicateInProcessFanOutTargetException(duplicateTopic.Key);
        }

        app.Register(services =>
        {
            foreach (var target in targets)
            {
                services.AddSingleton(new InProcessRouteReference(target.PipelineName));
            }
            services.TryAddSingletonImplementation<IStartUpCheck, InProcessRouteStartUpCheck>();
        });

        return app.Use(resolver => new InProcessFanOutClientMiddleware(
            targets,
            resolver.GetService<InProcessDispatcherRegistry>(),
            resolver.GetService<IServiceResolverFactory>(),
            resolver.GetService<ISerializer>(),
            resolver.GetService<ILogger<InProcessFanOutClientMiddleware>>()));
    }
}
