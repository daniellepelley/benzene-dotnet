using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.Clients;

/// <summary>
/// Builds the topic-keyed outbound routing table: one <see cref="IMiddlewarePipeline{TContext}"/>
/// per topic, replacing <c>ClientsBuilder</c>/<c>SingleClientsBuilder</c>'s split-by-cardinality
/// shape outright - "one client" is just the N=1 case of "many". Registered via
/// <c>AddOutboundRouting(...)</c>; see <c>work/archive/benzene-clients-redesign-plan-2026-07.md</c> §2.2.
/// </summary>
public class OutboundRoutingBuilder
{
    private readonly IBenzeneServiceContainer _benzeneServiceContainer;
    private readonly List<(string Topic, IMiddlewarePipelineBuilder<OutboundContext> Builder)> _routes = new();
    private readonly List<Action<IMiddlewarePipelineBuilder<OutboundContext>>> _everyRoute = new();
    private readonly HashSet<IMiddlewarePipelineBuilder<OutboundContext>> _prepended = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundRoutingBuilder"/> class.
    /// </summary>
    /// <param name="benzeneServiceContainer">The service container each route's pipeline builder registers dependencies into.</param>
    public OutboundRoutingBuilder(IBenzeneServiceContainer benzeneServiceContainer)
    {
        _benzeneServiceContainer = benzeneServiceContainer;
    }

    /// <summary>
    /// Registers an outbound pipeline for <paramref name="topic"/>.
    /// </summary>
    /// <param name="topic">The topic this pipeline sends messages for.</param>
    /// <param name="configure">Builds the pipeline - e.g. transport middleware plus cross-cutting
    /// concerns like retry/correlation/trace-context.</param>
    /// <returns>This builder, for chaining.</returns>
    public OutboundRoutingBuilder Route(string topic, Action<IMiddlewarePipelineBuilder<OutboundContext>> configure)
    {
        var builder = new MiddlewarePipelineBuilder<OutboundContext>(_benzeneServiceContainer);
        configure(builder);
        _routes.Add((topic, builder));
        return this;
    }

    /// <summary>
    /// Adds middleware to <b>every</b> route - those registered before this call and after it - ahead of
    /// each route's own middleware (it runs outermost). For a cross-cutting concern every outbound message
    /// must carry, such as the mesh's trace context (<c>Benzene.Mesh.Wire</c>'s
    /// <c>routing.UseMeshTraceContext()</c>), so no route can be left without it.
    /// </summary>
    /// <param name="configure">Adds the middleware to one route's pipeline; called once per route.</param>
    /// <returns>This builder, for chaining.</returns>
    public OutboundRoutingBuilder UseOnEveryRoute(Action<IMiddlewarePipelineBuilder<OutboundContext>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _everyRoute.Add(configure);
        return this;
    }

    /// <summary>Registers services with the container the routes are built against.</summary>
    /// <param name="action">The registration.</param>
    public void Register(Action<IBenzeneServiceContainer> action)
    {
        action(_benzeneServiceContainer);
    }

    /// <summary>
    /// Builds the final topic-keyed routing table.
    /// </summary>
    /// <returns>Each registered topic's built pipeline.</returns>
    /// <exception cref="DuplicateOutboundRouteException">The same topic was registered more than once.</exception>
    public IReadOnlyDictionary<string, IMiddlewarePipeline<OutboundContext>> Build()
    {
        var duplicate = _routes
            .GroupBy(x => x.Topic)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate != null)
        {
            throw new DuplicateOutboundRouteException(duplicate.Key);
        }

        foreach (var (_, builder) in _routes)
        {
            PrependEveryRouteMiddleware(builder);
        }

        return _routes.ToDictionary(x => x.Topic, x => x.Builder.Build());
    }

    // UseOnEveryRoute middleware runs ahead of the route's own: re-add the route's items after it.
    private void PrependEveryRouteMiddleware(IMiddlewarePipelineBuilder<OutboundContext> builder)
    {
        // Once per route, however many times Build() is called.
        if (_everyRoute.Count == 0 || builder is not MiddlewarePipelineBuilder<OutboundContext> pipelineBuilder || !_prepended.Add(builder))
        {
            return;
        }

        var own = pipelineBuilder.GetItems();
        pipelineBuilder.Clear();
        foreach (var configure in _everyRoute)
        {
            configure(pipelineBuilder);
        }

        foreach (var item in own)
        {
            pipelineBuilder.Use(item);
        }
    }
}
