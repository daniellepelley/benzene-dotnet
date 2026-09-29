using System.Text.Json.Nodes;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Mcp;

/// <summary>
/// The way a tool reaches the application: one message, through the same BenzeneMessage pipeline
/// every other transport dispatches into, in a scope of its own.
/// </summary>
/// <remarks>
/// Topic-bound tools use it implicitly. A custom tool body resolves it from the request scope and
/// asks as many questions as its job needs, so that every refusal the application makes lives in one
/// place (a handler, a middleware) and the tool layer never becomes a second implementation of the
/// rules that quietly disagrees with the first.
/// </remarks>
public interface IMcpMessageDispatcher
{
    /// <summary>Dispatches one message and returns the raw response envelope.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="body">The message body as JSON (an arguments object, typically); <c>null</c> for an empty body.</param>
    /// <param name="version">The message version, or empty for the default.</param>
    /// <param name="headers">Extra headers to send, if any.</param>
    /// <param name="cancellationToken">The transport's cancellation signal.</param>
    Task<IBenzeneMessageResponse> SendAsync(string topic, JsonNode? body, string version = "",
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>Dispatches one message and maps the response envelope to a tool result (<see cref="McpResults.ToToolResult"/>).</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="body">The message body as JSON.</param>
    /// <param name="version">The message version, or empty for the default.</param>
    /// <param name="headers">Extra headers to send, if any.</param>
    /// <param name="cancellationToken">The transport's cancellation signal.</param>
    Task<McpToolResult> AskAsync(string topic, JsonNode? body, string version = "",
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
}
