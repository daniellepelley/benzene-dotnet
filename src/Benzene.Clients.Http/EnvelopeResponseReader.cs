using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using Benzene.Results;

namespace Benzene.Clients.Http;

/// <summary>
/// How an envelope-over-HTTP client reads a response (transport-bindings.md §1, outbound clients): the
/// envelope in the body, <b>whatever the HTTP status</b> - the envelope endpoint answers 200 for every
/// envelope it processed, and a failure's outcome is in the envelope, not the status line. Only when the
/// body is not an envelope (a proxy's error page, a host's 401 before the envelope was read, an empty
/// body) does the HTTP status decide, through the reverse HTTP table (wire-contracts.md §4.1).
/// </summary>
internal static class EnvelopeResponseReader
{
    /// <summary>
    /// Reads the response as a client envelope: the body's envelope when it is one, else a status-only
    /// envelope mapped from <paramref name="httpStatus"/> by the reverse HTTP table.
    /// </summary>
    /// <param name="httpStatus">The HTTP response status.</param>
    /// <param name="body">The HTTP response body.</param>
    /// <param name="isEnvelope">Whether the body was an envelope.</param>
    public static BenzeneMessageClientResponse Read(HttpStatusCode httpStatus, string? body, out bool isEnvelope)
    {
        if (TryRead(body, out var envelope))
        {
            isEnvelope = true;
            return envelope;
        }

        isEnvelope = false;
        var mapped = httpStatus.Convert();
        return new BenzeneMessageClientResponse(mapped.Status, null!,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), mapped.IsSuccessful);
    }

    /// <summary>
    /// Parses <paramref name="body"/> as a response envelope (wire-contracts.md §1.2): a JSON object with a
    /// string <c>statusCode</c>. <c>headers</c> and <c>body</c> are read when present; an envelope without a
    /// boolean <c>isSuccessful</c> is malformed and read as unsuccessful - never classified from the status text.
    /// </summary>
    public static bool TryRead(string? body, out BenzeneMessageClientResponse envelope)
    {
        envelope = null!;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetProperty(root, "statusCode", out var statusCode)
                || statusCode.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var isSuccessful = TryGetProperty(root, "isSuccessful", out var successful)
                && successful.ValueKind == JsonValueKind.True;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (TryGetProperty(root, "headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headerElement.EnumerateObject())
                {
                    if (header.Value.ValueKind == JsonValueKind.String)
                    {
                        headers[header.Name] = header.Value.GetString()!;
                    }
                }
            }

            string? envelopeBody = null;
            if (TryGetProperty(root, "body", out var bodyElement))
            {
                envelopeBody = bodyElement.ValueKind switch
                {
                    JsonValueKind.String => bodyElement.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => bodyElement.GetRawText()
                };
            }

            envelope = new BenzeneMessageClientResponse(statusCode.GetString()!, envelopeBody!, headers, isSuccessful);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Case-insensitive on read, like every Benzene JSON reader of the envelope (writers emit camelCase).
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
