using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Accumulates one in-process <c>BenzeneMessage</c> pipeline per named module within a single
/// <c>AddInProcessMessaging(...)</c> call, mirroring how <c>OutboundRoutingBuilder.Route</c>
/// accumulates one outbound pipeline per topic within a single <c>AddOutboundRouting(...)</c> call.
/// </summary>
/// <remarks>
/// One call, many <see cref="Add(string,System.Action{IMiddlewarePipelineBuilder{BenzeneMessageContext}})"/>
/// calls is the contract, not one <c>AddInProcessMessaging(...)</c> call per module - see
/// <see cref="InProcessMessagingAlreadyRegisteredException"/> for why.
/// </remarks>
public class InProcessMessagingBuilder
{
    /// <summary>
    /// The pipeline name used by the parameterless <c>AddInProcessMessaging(configure)</c> overload
    /// and the parameterless <c>.UseInProcess()</c> outbound route - a single unnamed pipeline is the
    /// <see cref="DefaultName"/> case of "many, named".
    /// </summary>
    public const string DefaultName = "default";

    private readonly IBenzeneServiceContainer _benzeneServiceContainer;
    private readonly List<(string Name, IMiddlewarePipelineBuilder<BenzeneMessageContext> Builder)> _pipelines = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessMessagingBuilder"/> class.
    /// </summary>
    /// <param name="benzeneServiceContainer">The service container each pipeline's builder registers dependencies into.</param>
    public InProcessMessagingBuilder(IBenzeneServiceContainer benzeneServiceContainer)
    {
        _benzeneServiceContainer = benzeneServiceContainer;
    }

    /// <summary>
    /// Registers a named in-process pipeline - typically one module's handler assembly plus whatever
    /// cross-cutting middleware its topics need.
    /// </summary>
    /// <param name="name">
    /// The pipeline's name. Matched against the name passed to the outbound route's
    /// <c>.UseInProcess(name)</c>; each name may be added at most once (see
    /// <see cref="DuplicateInProcessPipelineException"/>).
    /// </param>
    /// <param name="configure">Configures the pipeline - typically at least <c>.UseMessageHandlers(...)</c>.</param>
    /// <returns>This builder, for chaining.</returns>
    public InProcessMessagingBuilder Add(string name, Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> configure)
    {
        var builder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(_benzeneServiceContainer);
        configure(builder);
        _pipelines.Add((name, builder));
        return this;
    }

    /// <summary>
    /// Registers the single unnamed (<see cref="DefaultName"/>) in-process pipeline - the shape a
    /// service with exactly one in-process module uses.
    /// </summary>
    /// <param name="configure">Configures the pipeline - typically at least <c>.UseMessageHandlers(...)</c>.</param>
    /// <returns>This builder, for chaining.</returns>
    public InProcessMessagingBuilder Add(Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> configure) =>
        Add(DefaultName, configure);

    /// <summary>
    /// Builds every registered pipeline and returns the name-keyed dispatcher set.
    /// </summary>
    /// <returns>Each registered name's built, transport-tagged dispatcher.</returns>
    /// <exception cref="DuplicateInProcessPipelineException">The same name was added more than once.</exception>
    internal IReadOnlyDictionary<string, IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>> Build()
    {
        var duplicate = _pipelines
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate != null)
        {
            throw new DuplicateInProcessPipelineException(duplicate.Key);
        }

        return _pipelines.ToDictionary(
            x => x.Name,
            x => BuildDispatcher(x.Builder),
            StringComparer.Ordinal);
    }

    private static IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse> BuildDispatcher(
        IMiddlewarePipelineBuilder<BenzeneMessageContext> builder)
    {
        var pipeline = builder.Build();
        var taggedPipeline = new TransportMiddlewarePipeline<BenzeneMessageContext>(TransportNames.InProcess, pipeline);

        return new MiddlewareApplication<IBenzeneMessageRequest, BenzeneMessageContext, IBenzeneMessageResponse>(
            taggedPipeline,
            @event => new BenzeneMessageContext(@event),
            context => context.BenzeneMessageResponse);
    }
}
