using System.Text.Json.Nodes;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Mcp;

/// <summary>
/// The default <see cref="IMcpMessageDispatcher"/>: builds a BenzeneMessage envelope and runs it
/// through the configured pipeline via <see cref="BenzeneMessageApplication"/>, one fresh DI scope
/// per message (the binding contract's scope rule).
/// </summary>
public sealed class McpMessageDispatcher : IMcpMessageDispatcher
{
    /// <summary>The header carrying the tool name a topic-bound dispatch came from.</summary>
    public const string ToolHeader = "mcp-tool";

    /// <summary>The header carrying the MCP session id, where the transport has one.</summary>
    public const string SessionHeader = "mcp-session";

    private readonly BenzeneMessageApplication _application;
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly string? _sessionId;

    /// <summary>Initializes a new instance of the <see cref="McpMessageDispatcher"/> class.</summary>
    /// <param name="pipeline">The built BenzeneMessage pipeline.</param>
    /// <param name="serviceResolverFactory">Creates the scope each message runs in.</param>
    /// <param name="sessionId">The current MCP session id, sent as <see cref="SessionHeader"/> when present.</param>
    public McpMessageDispatcher(IMiddlewarePipeline<BenzeneMessageContext> pipeline, IServiceResolverFactory serviceResolverFactory, string? sessionId = null)
    {
        _application = new BenzeneMessageApplication(pipeline);
        _serviceResolverFactory = serviceResolverFactory;
        _sessionId = sessionId;
    }

    /// <inheritdoc />
    public Task<IBenzeneMessageResponse> SendAsync(string topic, JsonNode? body, string version = "",
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach (var (key, value) in headers) requestHeaders[key] = value;
        }
        if (!string.IsNullOrEmpty(version)) requestHeaders[MessageVersionHeaders.Default] = version;
        if (_sessionId is not null) requestHeaders[SessionHeader] = _sessionId;

        var request = new BenzeneMessageRequest
        {
            Topic = topic,
            Headers = requestHeaders,
            Body = body?.ToJsonString() ?? "{}",
        };

        return _application.HandleAsync(request, _serviceResolverFactory, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<McpToolResult> AskAsync(string topic, JsonNode? body, string version = "",
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(topic, body, version, headers, cancellationToken);
        return McpResults.ToToolResult(response);
    }
}
