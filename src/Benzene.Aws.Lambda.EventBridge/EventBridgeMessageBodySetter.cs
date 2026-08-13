using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Aws.Lambda.EventBridge;

/// <summary>
/// Replaces the raw <c>detail</c> JSON of an EventBridge event with a claim-check-hydrated body.
/// </summary>
/// <remarks>
/// On EventBridge, headers and body share one physical slot: Benzene wire headers are embedded as
/// the reserved <see cref="EventBridgeMessageHeadersGetter.EmbeddedHeadersKey"/> object inside
/// <c>detail</c> (see <c>OutboundEventBridgeContextConverter.BuildDetail</c>/
/// <c>EventBridgeContextConverter{T}.BuildDetail</c> on the sender side), and
/// <see cref="EventBridgeMessageBodyGetter"/> returns <c>detail</c>'s raw JSON verbatim. A hydrated
/// body therefore has to have that same object re-embedded, mirroring the sender-side embed rule in
/// reverse - only when the original event actually carried the object AND the hydrated body is
/// itself a JSON object - or later pipeline steps that read headers via
/// <see cref="EventBridgeMessageHeadersGetter"/> (e.g. the version getter at request-mapping time)
/// would silently lose them. See <c>work/claim-check-plan.md</c> §1 "EventBridge subtlety".
/// </remarks>
public class EventBridgeMessageBodySetter : IMessageBodySetter<EventBridgeContext>
{
    /// <summary>
    /// Sets the hydrated body as the event's <c>detail</c>, re-embedding the original
    /// <c>_benzeneHeaders</c> object (if any) so header reads later in the pipeline still see it.
    /// </summary>
    /// <param name="context">The EventBridge context to set the body on.</param>
    /// <param name="body">The hydrated body to replace the event's raw <c>detail</c> JSON with.</param>
    public Task SetBody(EventBridgeContext context, string body)
    {
        var embeddedHeaders = ReadEmbeddedHeaders(context.Event.Detail);
        if (embeddedHeaders != null && JsonNode.Parse(body) is JsonObject hydrated)
        {
            hydrated[EventBridgeMessageHeadersGetter.EmbeddedHeadersKey] = embeddedHeaders;
            body = hydrated.ToJsonString();
        }

        using var document = JsonDocument.Parse(body);
        context.Event.Detail = document.RootElement.Clone();

        return Task.CompletedTask;
    }

    // Reads the original _benzeneHeaders object (if any) off the pre-hydration detail, detached from
    // its backing JsonDocument (JsonNode.Parse takes an independent copy of the raw text) so it
    // survives past this call irrespective of what happens to the original document.
    private static JsonObject ReadEmbeddedHeaders(JsonElement originalDetail)
    {
        if (originalDetail.ValueKind != JsonValueKind.Object ||
            !originalDetail.TryGetProperty(EventBridgeMessageHeadersGetter.EmbeddedHeadersKey, out var embedded) ||
            embedded.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonNode.Parse(embedded.GetRawText()) as JsonObject;
    }
}
