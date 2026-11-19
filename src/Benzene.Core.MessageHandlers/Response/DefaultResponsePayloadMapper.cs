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
/// document on failure (<see cref="ProblemTypes.ForResult"/>: a handler-authored document emitted
/// after coherence fill-in, or one synthesized from the result's status and errors). A registered
/// <see cref="IProblemFormatter{TContext}"/> replaces the failure body's format.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type the result was produced for.</typeparam>
public class DefaultResponsePayloadMapper<TContext> : IResponsePayloadMapper<TContext>
{
    private readonly IProblemFormatter<TContext>? _problemFormatter;

    /// <summary>Initializes a new instance of the <see cref="DefaultResponsePayloadMapper{TContext}"/> class.</summary>
    /// <param name="problemFormatter">The context's failure-body formatter, when the application registered one.</param>
    public DefaultResponsePayloadMapper(IProblemFormatter<TContext>? problemFormatter = null)
    {
        _problemFormatter = problemFormatter;
    }

    /// <summary>
    /// Maps a handler's result into a serialized response body: the success payload if
    /// <see cref="IBenzeneResult.IsSuccessful"/>, otherwise the failure body.
    /// </summary>
    /// <param name="context">The transport context, passed to the problem formatter.</param>
    /// <param name="messageHandlerResult">The outcome of routing and invoking the handler.</param>
    /// <param name="serializer">The serializer to use to produce the response body.</param>
    /// <returns>
    /// The serialized response body. A failure always has one (wire-contracts §1.3), including a
    /// rejection made before any handler was resolved. A success has none when the payload is
    /// <c>null</c> or no handler was resolved, because there is no response type to serialize it with.
    /// </returns>
    public string Map(TContext context, IMessageHandlerResult messageHandlerResult, ISerializer serializer)
    {
        var result = messageHandlerResult.BenzeneResult;
        if (!result.IsSuccessful)
        {
            return ProblemPayloads.Serialize(context, result, ProblemTypes.ForResult(result), serializer, _problemFormatter);
        }

        if (messageHandlerResult.MessageHandlerDefinition == null)
        {
            return null;
        }

        return SerializePayload(messageHandlerResult.MessageHandlerDefinition.ResponseType, result.PayloadAsObject, serializer);
    }

    private string SerializePayload(Type type, object? payload, ISerializer serializer)
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
