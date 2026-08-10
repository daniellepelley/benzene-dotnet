using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Clients.Common;

namespace Benzene.Clients;

/// <summary>
/// The default <see cref="IBenzeneMessageSender"/>: resolves a topic to its registered outbound
/// pipeline and runs it. See <c>work/benzene-clients-redesign-plan.md</c> §2.3.
/// </summary>
internal class DefaultBenzeneMessageSender : IBenzeneMessageSender
{
    private readonly IReadOnlyDictionary<string, IMiddlewarePipeline<OutboundContext>> _routes;
    private readonly IServiceResolver _serviceResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultBenzeneMessageSender"/> class.
    /// </summary>
    /// <param name="routes">The topic-keyed routing table built by <see cref="OutboundRoutingBuilder"/>.</param>
    /// <param name="serviceResolver">The service resolver each send's pipeline run uses.</param>
    public DefaultBenzeneMessageSender(IReadOnlyDictionary<string, IMiddlewarePipeline<OutboundContext>> routes, IServiceResolver serviceResolver)
    {
        _routes = routes;
        _serviceResolver = serviceResolver;
    }

    /// <inheritdoc />
    public async Task<IBenzeneResult<TResponse>> SendAsync<TRequest, TResponse>(string topic, TRequest request, IDictionary<string, string>? headers = null)
    {
        if (!_routes.TryGetValue(topic, out var pipeline))
        {
            throw new UnroutedTopicException(topic);
        }

        var context = new OutboundContext(topic, request!, headers);
        await pipeline.HandleAsync(context, _serviceResolver);

        if (context.Response is IBenzeneResult<TResponse> typedResponse)
        {
            return typedResponse;
        }

        // A transport whose MapResponseAsync couldn't produce an already-typed IBenzeneResult<TResponse>
        // (it doesn't know TResponse - only the caller does) may instead leave the raw envelope on the
        // context. Deserialize it here, now that TResponse is known, rather than requiring every such
        // transport to solve typed-response deserialization itself.
        if (context.Response is BenzeneMessageClientResponse rawResponse)
        {
            return rawResponse.AsBenzeneResult<TResponse>(_serviceResolver.GetService<ISerializer>());
        }

        throw new OutboundResponseTypeMismatchException(topic, OutboundResponseTypeMismatchException.GetActualResponseType(context.Response), typeof(TResponse));
    }
}
