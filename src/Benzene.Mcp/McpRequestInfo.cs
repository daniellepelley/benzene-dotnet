using System.Text.Json;
using System.Text.Json.Nodes;

namespace Benzene.Mcp;

/// <summary>
/// What can be read off a request body before the protocol answers it: the method, and the tool a
/// <c>tools/call</c> names. For the middleware that has to decide something (a scope, an audit line)
/// about a call before it runs.
/// </summary>
/// <param name="Method">The JSON-RPC method.</param>
/// <param name="ToolName">The tool a <c>tools/call</c> names, or <c>null</c> for any other method.</param>
public sealed record McpRequestInfo(string Method, string? ToolName)
{
    /// <summary>Whether this is the <c>initialize</c> request that opens a session.</summary>
    public bool IsInitialize => Method == "initialize";

    /// <summary>Whether this is a <c>tools/call</c>.</summary>
    public bool IsToolCall => Method == "tools/call";

    /// <summary>
    /// Reads the method and tool name, or returns <c>null</c> for a body that is not a JSON-RPC
    /// request object. A body that cannot be parsed is about to be answered with a parse error by the
    /// protocol itself, so it needs no decision from anybody else.
    /// </summary>
    /// <param name="body">The request body.</param>
    public static McpRequestInfo? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(body); }
        catch (JsonException) { return null; }

        if (parsed is not JsonObject request) return null;
        var method = request["method"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null;
        if (method is null) return null;

        var toolName = method == "tools/call" && (request["params"] as JsonObject)?["name"] is JsonValue n
                       && n.GetValueKind() == JsonValueKind.String
            ? n.GetValue<string>()
            : null;
        return new McpRequestInfo(method, toolName);
    }
}
