using System.Text.Json.Nodes;

namespace Benzene.Mcp;

/// <summary>One <c>tools/call</c> as it arrived: which tool, with what arguments, from which session.</summary>
/// <param name="Name">The tool name the client asked for.</param>
/// <param name="Arguments">The <c>arguments</c> object, or an empty object when the client sent none.</param>
/// <param name="SessionId">
/// The <c>Mcp-Session-Id</c> the transport carries, or <c>null</c> where the transport keeps none
/// (stdio) or the client sent none. The library issues and validates it; what it means to the
/// application (a chosen tenant, a conversation's working set) is the application's own.
/// </param>
public sealed record McpToolCall(string Name, JsonObject Arguments, string? SessionId);
