using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Middleware that dispatches one outbound send to several named in-process pipelines concurrently,
/// each under its own <see cref="InProcessFanOutTarget.Topic"/> - the in-monolith equivalent of SNS
/// fanning one topic out to several subscribers. Terminates the outbound pipeline directly against
/// <see cref="OutboundContext"/> (no context conversion needed: unlike a single-target
/// <c>.UseInProcess(name)</c>, there is no one target response to map back).
/// </summary>
/// <remarks>
/// See <c>work/archive/inprocess-fanout-design-2026-08.md</c> for the semantics this implements and what it
/// deliberately does not solve (no in-process DLQ/redelivery for a failed consumer), and
/// <see cref="InProcessFanOutTarget"/>/<see cref="DuplicateInProcessFanOutTargetException"/> for why
/// each target dispatches under its own topic rather than the route's literal topic.
/// </remarks>
public class InProcessFanOutClientMiddleware : IMiddleware<OutboundContext>, ITerminalMiddleware
{
    private readonly IReadOnlyList<InProcessFanOutTarget> _targets;
    private readonly InProcessDispatcherRegistry _registry;
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly ISerializer _serializer;
    private readonly ILogger<InProcessFanOutClientMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessFanOutClientMiddleware"/> class.
    /// </summary>
    /// <param name="targets">Every pipeline/topic pair this route fans out to.</param>
    /// <param name="registry">Resolves each target's pipeline name to its dispatcher.</param>
    /// <param name="serviceResolverFactory">
    /// Used to give each dispatched consumer its own fresh DI scope - the same isolation a message
    /// sent over a real transport would get in each receiving process, not the sending call's scope,
    /// and not shared between concurrently-dispatched consumers either.
    /// </param>
    /// <param name="serializer">The serializer used to build each target's request body.</param>
    /// <param name="logger">Logs each consumer's failure - the only visibility a failure gets, since there is no in-process DLQ.</param>
    public InProcessFanOutClientMiddleware(
        IReadOnlyList<InProcessFanOutTarget> targets,
        InProcessDispatcherRegistry registry,
        IServiceResolverFactory serviceResolverFactory,
        ISerializer serializer,
        ILogger<InProcessFanOutClientMiddleware> logger)
    {
        _targets = targets;
        _registry = registry;
        _serviceResolverFactory = serviceResolverFactory;
        _serializer = serializer;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => nameof(InProcessFanOutClientMiddleware);

    /// <summary>
    /// Dispatches to every target concurrently, each under its own topic, and sets an unconditional
    /// <see cref="Void"/> success response - matching what a real SNS publish returns (accepted, with
    /// no visibility into subscriber outcomes). This is a terminal middleware; it does not call
    /// <paramref name="next"/>.
    /// </summary>
    /// <param name="context">The context carrying the request to fan out and to receive the response.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        // Resolve every target's dispatcher up front, outside the per-consumer try/catch below: a
        // typo'd or unregistered pipeline name is a wiring mistake (the same
        // InProcessPipelineNotFoundException a single-target .UseInProcess(name) throws), not a
        // consumer failure to isolate and swallow.
        var dispatches = _targets
            .Select(target => (
                target.PipelineName,
                target.Topic,
                Dispatcher: _registry.Resolve(target.PipelineName),
                Request: InProcessRequestBuilder.Build(context, _serializer, target.Topic)))
            .ToArray();

        await Task.WhenAll(dispatches.Select(d => DispatchAsync(d.PipelineName, d.Dispatcher, d.Request, d.Topic)));

        context.Response = BenzeneResult.Ok<Void>();
    }

    private async Task DispatchAsync(
        string pipelineName,
        IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse> dispatcher,
        IBenzeneMessageRequest request,
        string topic)
    {
        try
        {
            var response = await dispatcher.HandleAsync(request, _serviceResolverFactory);
            if (!BenzeneResultStatus.IsSuccess(response.StatusCode))
            {
                // A baseline failure signal even when no logging middleware is wired on the consumer's
                // own pipeline - matching MessageRouter's own precedent for an unsuccessful handler
                // result. There is no in-process DLQ, so this is the only place the failure is visible.
                _logger.LogWarning(
                    "In-process fan-out consumer '{PipelineName}' returned unsuccessful status {StatusCode} for topic {Topic}",
                    pipelineName, response.StatusCode, topic);
            }
        }
        catch (Exception ex)
        {
            // Isolated from the other consumers and from the caller by design - one failing reaction
            // must not affect delivery to the others or the fan-out's own (always-success) response.
            _logger.LogWarning(ex,
                "In-process fan-out consumer '{PipelineName}' threw for topic {Topic}", pipelineName, topic);
        }
    }
}
