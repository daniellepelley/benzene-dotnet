using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Clients.Common;

namespace Benzene.Clients;

/// <summary>
/// The default <see cref="IBenzeneMessageSender"/>: resolves a topic to its registered outbound
/// pipeline and runs it. See <c>work/archive/benzene-clients-redesign-plan-2026-07.md</c> §2.3.
/// </summary>
internal class DefaultBenzeneMessageSender : IBenzeneMessageSender
{
    private static readonly ISerializer DefaultResponseSerializer = new JsonSerializer();

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
        try
        {
            await pipeline.HandleAsync(context, _serviceResolver);
        }
        catch (TimeoutException exception)
        {
            // The route's own deadline (a UseTimeout() on the route) elapsed before the transport answered:
            // a timeout result, not an exception (transport-bindings §1, spec 1.1).
            return TransportFailure.ToResult<TResponse>(exception);
        }

        if (context.Response is IBenzeneResult<TResponse> typedResponse)
        {
            return typedResponse;
        }

        // The route's transport failed before a reply (or, one-way, before the broker accepted the
        // message): a timeout/service-unavailable result, not an exception (transport-bindings §1, spec 1.1).
        // Any TResponse - a failure carries no payload.
        if (context.Response is TransportFailure transportFailure)
        {
            return transportFailure.ToResult<TResponse>();
        }

        // A transport whose MapResponseAsync couldn't produce an already-typed IBenzeneResult<TResponse>
        // (it doesn't know TResponse - only the caller does) may instead leave the raw envelope on the
        // context. Deserialize it here, now that TResponse is known, rather than requiring every such
        // transport to solve typed-response deserialization itself.
        if (context.Response is BenzeneMessageClientResponse rawResponse)
        {
            // TryGetService, with the same JSON default every outbound converter in this package uses
            // when no serializer is supplied: an outbound-only container (no AddBenzene(), no message
            // handlers) has nothing registering ISerializer, and the send side would still have
            // serialized the request as JSON. Failing here would be a message-path failure for a
            // registration the send direction never needed. Register your own ISerializer to override.
            return rawResponse.AsBenzeneResult<TResponse>(_serviceResolver.TryGetService<ISerializer>() ?? DefaultResponseSerializer);
        }

        throw new OutboundResponseTypeMismatchException(topic, OutboundResponseTypeMismatchException.GetActualResponseType(context.Response), typeof(TResponse));
    }
}
