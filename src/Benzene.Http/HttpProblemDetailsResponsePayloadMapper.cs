using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Http;

/// <summary>
/// Decorates a transport's <see cref="IResponsePayloadMapper{TContext}"/> so a failed result's
/// problem document carries the numeric RFC 9457 <see cref="ProblemDetails.Status"/> - HTTP-facing
/// transports only (docs/specification/wire-contracts.md §2.1/§2.3; work/problem-details-plan.md
/// Phase 4). <see cref="ProblemDetails.Status"/> is filled in via the <b>same</b>
/// <see cref="IHttpStatusCodeMapper"/> instance <see cref="HttpStatusCodeResponseHandler{TContext}"/>
/// uses to set the actual HTTP response status line, so the body's <c>status</c> member and the real
/// response code are derived from one mapping and can never disagree.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type the result was produced for.</typeparam>
/// <remarks>
/// Success responses, and results with no resolved <see cref="IMessageHandlerResult.MessageHandlerDefinition"/>,
/// delegate straight through to <see cref="Inner"/> (registered as the transport-neutral
/// <c>DefaultResponsePayloadMapper{TContext}</c> by <see cref="Extensions.UseHttpProblemDetailsStatus{TContext}"/>)
/// unchanged. Only the failure branch is re-implemented here (building the same
/// <see cref="ProblemTypes.From"/> document, then adding <see cref="ProblemDetails.Status"/>) rather
/// than post-processing <see cref="Inner"/>'s already-serialized output, so this works uniformly
/// across every negotiated serializer without a deserialize/mutate/reserialize round trip.
/// </remarks>
public class HttpProblemDetailsResponsePayloadMapper<TContext> : IResponsePayloadMapper<TContext>
{
    private readonly IResponsePayloadMapper<TContext> _inner;
    private readonly IHttpStatusCodeMapper _httpStatusCodeMapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpProblemDetailsResponsePayloadMapper{TContext}"/> class.
    /// </summary>
    /// <param name="inner">The transport-neutral mapper to delegate to for every case this decorator doesn't change.</param>
    /// <param name="httpStatusCodeMapper">The same mapper the transport uses to set the HTTP response status line.</param>
    public HttpProblemDetailsResponsePayloadMapper(IResponsePayloadMapper<TContext> inner, IHttpStatusCodeMapper httpStatusCodeMapper)
    {
        _inner = inner;
        _httpStatusCodeMapper = httpStatusCodeMapper;
    }

    /// <summary>The wrapped, transport-neutral mapper every non-failure case delegates to.</summary>
    public IResponsePayloadMapper<TContext> Inner => _inner;

    /// <inheritdoc />
    public string Map(TContext context, IMessageHandlerResult messageHandlerResult, ISerializer serializer)
    {
        if (messageHandlerResult.MessageHandlerDefinition == null || messageHandlerResult.BenzeneResult.IsSuccessful)
        {
            return _inner.Map(context, messageHandlerResult, serializer);
        }

        var result = messageHandlerResult.BenzeneResult;
        var problem = ProblemTypes.From(result);
        problem.Status = int.Parse(_httpStatusCodeMapper.Map(result.Status, result.IsSuccessful));

        return serializer.Serialize(problem);
    }
}
