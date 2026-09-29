using System.Text.Json;
using System.Text.Json.Nodes;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Mcp;

/// <summary>Maps a BenzeneMessage response envelope to what a model is shown.</summary>
public static class McpResults
{
    /// <summary>
    /// A successful envelope becomes the body as text (or the status, when there is no body); a
    /// failed one becomes a refusal naming the status and the most specific message the body carries
    /// (a problem document's <c>detail</c>, an <c>errors[].message</c> list, a <c>message</c>, or the body itself).
    /// </summary>
    /// <param name="response">The response envelope.</param>
    public static McpToolResult ToToolResult(IBenzeneMessageResponse response)
    {
        if (response.IsSuccessful)
        {
            return McpToolResult.Ok(string.IsNullOrWhiteSpace(response.Body) ? response.StatusCode : response.Body);
        }

        var status = string.IsNullOrWhiteSpace(response.StatusCode) ? "failed" : response.StatusCode;
        var why = Explain(response.Body) ?? $"The service answered '{status}'.";
        return McpToolResult.Refused($"{status}: {why}");
    }

    /// <summary>The most specific human-readable message a failure body carries, or <c>null</c> for an empty body.</summary>
    /// <param name="body">The response body.</param>
    public static string? Explain(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(body); }
        catch (JsonException) { return body.Trim(); }

        if (parsed is not JsonObject json) return body.Trim();

        if (Text(json["detail"]) is { } detail) return detail;

        if (json["errors"] is JsonArray errors)
        {
            var messages = errors.OfType<JsonObject>()
                .Select(e => Text(e["message"]) ?? Text(e["detail"]))
                .Where(m => m is not null)
                .ToArray();
            if (messages.Length > 0) return string.Join(" ", messages);
        }

        if (Text(json["message"]) is { } message) return message;
        if (Text(json["title"]) is { } title) return title;
        return body.Trim();
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
