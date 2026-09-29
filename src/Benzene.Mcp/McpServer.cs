using System.Text.Json;
using System.Text.Json.Nodes;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Mcp;

/// <summary>
/// The Model Context Protocol over JSON-RPC 2.0, with no transport attached: <c>initialize</c>,
/// <c>tools/list</c>, <c>tools/call</c>, <c>ping</c>, and silence for notifications. The Streamable
/// HTTP middleware and the stdio worker both drive this one class, and neither knows about the other.
/// </summary>
/// <remarks>
/// The protocol surface a tool server needs is small, so the cost of owning it is a page, and what it
/// buys is that the same server answers a line on stdin from a desktop client and a POST body from
/// API Gateway, Azure Functions or ASP.NET Core without the application being any of those things.
/// </remarks>
public sealed class McpServer
{
    /// <summary>
    /// Protocol versions this server speaks, newest first. A client asks for one; if it is on this
    /// list it gets it back, and otherwise it gets the newest we speak and decides for itself.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtocolVersions = new[] { "2025-06-18", "2025-03-26" };

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private readonly McpOptions _options;
    private readonly IMiddlewarePipeline<BenzeneMessageContext> _pipeline;
    private readonly Action<string> _log;

    /// <summary>Initializes a new instance of the <see cref="McpServer"/> class.</summary>
    /// <param name="options">The tools, identity and hooks.</param>
    /// <param name="pipeline">The built BenzeneMessage pipeline topic-bound tools dispatch into.</param>
    /// <param name="log">Where unexpected failures go; falls back to <see cref="McpOptions.Log"/>, then to nothing.</param>
    public McpServer(McpOptions options, IMiddlewarePipeline<BenzeneMessageContext> pipeline, Action<string>? log = null)
    {
        _options = options;
        _pipeline = pipeline;
        _log = log ?? options.Log ?? (_ => { });
    }

    /// <summary>The tools this server advertises.</summary>
    public IReadOnlyList<McpToolDefinition> Tools => _options.Tools;

    /// <summary>
    /// Answers one message. <c>null</c> means the message was a notification (something the client tells
    /// us and expects no reply to) and the transport should send nothing back rather than an empty frame.
    /// </summary>
    /// <param name="requestJson">The JSON-RPC request.</param>
    /// <param name="resolver">The per-request DI scope; custom tool bodies run in it and topic-bound tools spawn their own scope from its factory.</param>
    /// <param name="sessionId">The transport's session id, or <c>null</c> where there is none.</param>
    /// <param name="cancellationToken">The transport's cancellation signal.</param>
    public async Task<JsonObject?> HandleAsync(string requestJson, IServiceResolver resolver, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(requestJson); }
        catch (JsonException e) { return Error(null, ParseError, $"That is not JSON: {e.Message}"); }

        if (parsed is not JsonObject request) return Error(null, InvalidRequest, "A request has to be a JSON object.");

        var id = request["id"]?.DeepClone();
        var method = request["method"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null;
        if (method is null) return Error(id, InvalidRequest, "A request has to name a method.");

        // A notification has no id, and the protocol says not to answer one.
        var isNotification = !request.ContainsKey("id");
        var parameters = request["params"] as JsonObject ?? new JsonObject();

        try
        {
            var result = method switch
            {
                "initialize" => Initialize(parameters),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(parameters, resolver, sessionId, cancellationToken),
                "ping" => new JsonObject(),
                _ => null,
            };

            if (isNotification) return null;
            if (result is null) return Error(id, MethodNotFound, $"This server does not do '{method}'.");
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpRequestException e)
        {
            return isNotification ? null : Error(id, InvalidParams, e.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Not a refusal (those arrive as McpRequestException above, or as a tool result) but something
            // nobody meant to happen, whose message can be a database's own words about its tables. The
            // detail is logged against a reference, and the client is given the reference and nothing else.
            var reference = Guid.NewGuid().ToString("N")[..8];
            _log($"{reference} unexpected failure answering '{method}': {e}");
            return isNotification ? null : Error(id, InternalError,
                $"Something went wrong answering that, and it has been logged as {reference}. It was not a refusal: "
                + "trying again may work, and if it does not, that reference is what to quote.");
        }
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        var asked = parameters["protocolVersion"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        var result = new JsonObject
        {
            ["protocolVersion"] = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = _options.ServerInfo.Name, ["version"] = _options.ServerInfo.Version },
        };
        if (!string.IsNullOrWhiteSpace(_options.ServerInfo.Instructions))
        {
            result["instructions"] = _options.ServerInfo.Instructions;
        }
        return result;
    }

    private JsonObject ListTools()
    {
        var list = new JsonArray();
        foreach (var tool in _options.Tools)
        {
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
                // Advertised so a client can leave the reading tools alone and put the ones that write
                // behind an approval.
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = !tool.Writes,
                    ["destructiveHint"] = tool.Writes && tool.Destructive,
                    ["idempotentHint"] = !tool.Writes,
                },
            });
        }
        return new JsonObject { ["tools"] = list };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject parameters, IServiceResolver resolver, string? sessionId,
        CancellationToken cancellationToken)
    {
        var name = parameters["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
        if (string.IsNullOrWhiteSpace(name)) throw new McpRequestException("A tool call has to name a tool.");
        var tool = _options.Tools.FirstOrDefault(t => t.Name == name)
            ?? throw new McpRequestException($"There is no tool called '{name}'.");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        var call = new McpToolCall(name, arguments, sessionId);

        var dispatcher = new McpMessageDispatcher(_pipeline, resolver.GetService<IServiceResolverFactory>(), sessionId);

        McpToolResult result;
        try
        {
            result = tool.Invoke is not null
                ? await tool.Invoke(call, new McpToolContext(resolver, dispatcher), cancellationToken)
                : await dispatcher.AskAsync(tool.Topic!, arguments, tool.Version,
                    new Dictionary<string, string> { [McpMessageDispatcher.ToolHeader] = tool.Name }, cancellationToken);
        }
        catch (McpRequestException e)
        {
            // The arguments were wrong in a way the model can fix, so it reads it as a tool result rather
            // than a protocol error it would never be shown.
            result = McpToolResult.Refused(e.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (_options.ToolFailure?.Invoke(e) is { } refusal)
        {
            // The application saying no in its own words (a domain exception the app mapped). An answer the
            // model reads is one it can act on, where a transport error is one it never sees.
            result = refusal;
        }

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError,
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
