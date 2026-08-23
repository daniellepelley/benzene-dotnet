using System;
using System.Collections.Generic;
using System.Text.Json;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Aws.Lambda.Core;

namespace Benzene.Aws.Lambda.EventBridge;

/// <summary>
/// Maps the event onto Benzene headers (plan decision E4): envelope metadata under
/// <c>eventbridge-</c>-prefixed keys, plus any Benzene wire headers (correlation, <c>traceparent</c>, ...)
/// lifted verbatim from the reserved <c>_benzeneHeaders</c> object inside <c>detail</c> — EventBridge has
/// no native per-message attributes, so that's where the outbound client embeds them.
/// </summary>
public class EventBridgeMessageHeadersGetter : IMessageHeadersGetter<EventBridgeContext>
{
    /// <summary>The reserved key inside <c>detail</c> that carries embedded Benzene wire headers.</summary>
    public const string EmbeddedHeadersKey = "_benzeneHeaders";

    /// <summary>
    /// Gets the event's envelope metadata, plus any embedded Benzene wire headers, as headers.
    /// </summary>
    /// <param name="context">The EventBridge context to extract headers from.</param>
    /// <returns>A dictionary of header names to values.</returns>
    public IDictionary<string, string> GetHeaders(EventBridgeContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var @event = context.Event;

        headers.AddIfPresent("eventbridge-id", @event.Id);
        headers.AddIfPresent("eventbridge-source", @event.Source);
        headers.AddIfPresent("eventbridge-account", @event.Account);
        headers.AddIfPresent("eventbridge-region", @event.Region);
        headers.AddIfPresent("eventbridge-time", @event.Time);
        headers.AddIfPresent("eventbridge-detail-type", @event.DetailType);

        if (@event.Detail.ValueKind == JsonValueKind.Object &&
            @event.Detail.TryGetProperty(EmbeddedHeadersKey, out var embedded) &&
            embedded.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in embedded.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    // GetString() is only null when ValueKind is Null, already excluded above.
                    headers[property.Name] = property.Value.GetString()!;
                }
            }
        }

        return headers;
    }
}
