using System;
using System.Text.Json;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Aws.Lambda.EventBridge;

/// <summary>
/// The message body is the raw JSON text of the event's <c>detail</c> — the domain payload
/// (plan decision E3). The reserved <c>_benzeneHeaders</c> key, when present, is an extra field the
/// request mapper's deserialization simply ignores.
/// </summary>
public class EventBridgeMessageBodyGetter : IMessageBodyGetter<EventBridgeContext>
{
    public string? GetBody(EventBridgeContext context)
    {
        var detail = context.Event.Detail;

        switch (detail.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                // No detail field at all, or an explicit JSON null - either way there's no body to
                // deserialize. Previously JsonValueKind.Null fell through to the default GetRawText()
                // case below, handing a handler the literal 4-character string "null" as its body
                // instead of no body at all.
                return null;

            case JsonValueKind.String:
                // A string-typed detail is a malformed/synthetic delivery - real EventBridge always
                // delivers detail as an object. It's typically a producer mistake: JSON that got
                // serialized into a string before being assigned to "detail". GetRawText() on a
                // JsonValueKind.String returns the *escaped* JSON string literal (quotes and all),
                // which no handler could deserialize into its request type - so unwrap it instead.
                return UnwrapStringDetail(detail);

            default:
                return detail.GetRawText();
        }
    }

    private static string UnwrapStringDetail(JsonElement detail)
    {
        var text = detail.GetString();

        if (string.IsNullOrEmpty(text) || !TryParseJson(text, out var parsed))
        {
            throw new InvalidOperationException(
                "EventBridge event detail was a JSON string, but its content is not itself valid JSON, " +
                "so it could not be unwrapped into a message body.");
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"EventBridge event detail was a JSON string wrapping a {parsed.RootElement.ValueKind} value, " +
                    "not a JSON object - only an object can be unwrapped into a message body.");
            }

            return parsed.RootElement.GetRawText();
        }
    }

    private static bool TryParseJson(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
