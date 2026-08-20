using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Core.MessageHandlers.Response;

/// <summary>
/// Default <see cref="IResponsePayloadMapper{TContext}"/> implementation: serializes the handler's
/// success payload using its declared response type, or an RFC 9457 <see cref="ProblemDetails"/>
/// document on failure - a handler-authored document (<see cref="IHasProblemDetails"/>, e.g.
/// <see cref="BenzeneResult.Problem{T}"/>) emitted verbatim after coherence fill-in, or one
/// synthesized from the result's status and errors (<see cref="ProblemTypes.From"/>) otherwise.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type the result was produced for.</typeparam>
public class DefaultResponsePayloadMapper<TContext> : IResponsePayloadMapper<TContext>
{
    /// <summary>
    /// Maps a handler's result into a serialized response body: the success payload if
    /// <see cref="IBenzeneResult.IsSuccessful"/>, otherwise a serialized problem document
    /// (see <see cref="BuildProblem"/>).
    /// </summary>
    /// <param name="context">Unused; the mapping does not depend on the transport context.</param>
    /// <param name="messageHandlerResult">The outcome of routing and invoking the handler.</param>
    /// <param name="serializer">The serializer to use to produce the response body.</param>
    /// <returns>
    /// The serialized response body, or <c>null</c> if <see cref="IMessageHandlerResultBase.MessageHandlerDefinition"/>
    /// is <c>null</c> (no handler was resolved) or the success payload itself is <c>null</c>.
    /// </returns>
    public string Map(TContext context, IMessageHandlerResult messageHandlerResult, ISerializer serializer)
    {
        if (messageHandlerResult.MessageHandlerDefinition == null)
        {
            return null;
        }

        return messageHandlerResult.BenzeneResult.IsSuccessful
            ? SerializePayload(messageHandlerResult.MessageHandlerDefinition.ResponseType, messageHandlerResult.BenzeneResult.PayloadAsObject, serializer)
            : serializer.Serialize(BuildProblem(messageHandlerResult.BenzeneResult));
    }

    /// <summary>
    /// Builds the problem document to emit for a failed result (work/archive/problem-details-plan-2026-08.md Phase 3
    /// step 4): when the result implements <see cref="IHasProblemDetails"/> with a non-null
    /// <see cref="IHasProblemDetails.Problem"/> - a handler that deliberately authored one via
    /// <see cref="BenzeneResult.Problem{T}"/> (custom <see cref="ProblemDetails.Type"/>/<see
    /// cref="ProblemDetails.Instance"/>, or an application subclass carrying extension members) - that
    /// exact instance is returned (preserving its runtime type, so subclass extension members still
    /// serialize) with any member the handler left absent backfilled from the registry, keyed by the
    /// same <see cref="IBenzeneResult.Status"/> the envelope/HTTP status line already uses so the two
    /// can never disagree. Every other failed result (the overwhelming majority) gets a document
    /// synthesized fresh via <see cref="ProblemTypes.From"/>.
    /// </summary>
    private static ProblemDetails BuildProblem(IBenzeneResult result)
    {
        if (result is IHasProblemDetails { Problem: { } problem })
        {
            problem.Type ??= ProblemTypes.TypeFor(result.Status);
            problem.Title ??= ProblemTypes.TitleFor(result.Status);
            problem.BenzeneStatus ??= result.Status;

            return problem;
        }

        return ProblemTypes.From(result);
    }

    private string SerializePayload(Type type, object payload, ISerializer serializer)
    {
        if (payload == null)
        {
            return null;
        }

        if (payload is IRawStringMessage rawStringMessage)
        {
            return rawStringMessage.Content;
        }

        return serializer.Serialize(type, payload);
    }
}
