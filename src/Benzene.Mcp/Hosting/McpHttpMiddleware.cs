using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Http;
using Microsoft.Extensions.Logging;

namespace Benzene.Mcp.Hosting;

/// <summary>
/// MCP over Streamable HTTP on any Benzene HTTP transport (API Gateway, Azure Functions, ASP.NET
/// Core, self-host): a POST to the configured path is one JSON-RPC message answered by
/// <see cref="McpServer"/>; any other path falls through to <c>next</c>. Drives the transport-neutral
/// request/response adapters directly, the same short-circuit shape as the BenzeneMessage HTTP endpoint.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
/// <remarks>
/// <para>
/// Sessions: <c>initialize</c> is answered with a fresh <c>Mcp-Session-Id</c>; any later request that
/// carries one this server could have issued (32 hex characters) has it echoed back and passed to the
/// tools as <see cref="McpToolCall.SessionId"/>. The server keeps no session state itself: what the id
/// means (a chosen tenant, a working set) is the application's own, keyed on the id.
/// </para>
/// <para>
/// A notification is answered with <c>202 Accepted</c> and an empty body, as the protocol asks. A
/// <c>GET</c> (a client opening a server-to-client stream) or a <c>DELETE</c> (ending a session) on the
/// path is answered <c>405</c>: this binding offers neither, and the protocol says a server that does
/// not must say so rather than fall through.
/// </para>
/// <para>
/// Security: the tools reach the pipeline like any other transport, so put authentication middleware
/// in front of this one and do not expose it unauthenticated in production. Read the tool a call names
/// with <see cref="McpRequestInfo.TryParse"/> and its <see cref="McpToolDefinition.Writes"/> from the
/// registered <see cref="McpToolCatalog"/> when an authorizer needs to gate writes separately.
/// </para>
/// </remarks>
public class McpHttpMiddleware<TContext> : IMiddleware<TContext>, ITerminalMiddleware where TContext : IHttpContext
{
    /// <summary>The header the MCP transport carries its session in.</summary>
    public const string SessionHeader = "Mcp-Session-Id";

    private readonly string _path;
    private readonly McpServer _server;
    private readonly IServiceResolver _serviceResolver;
    private readonly IHttpRequestAdapter<TContext> _httpRequestAdapter;
    private readonly IMessageBodyGetter<TContext> _messageBodyGetter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;

    /// <summary>Initializes a new instance of the <see cref="McpHttpMiddleware{TContext}"/> class.</summary>
    /// <param name="options">The server's tools, identity and path.</param>
    /// <param name="pipeline">The built BenzeneMessage pipeline topic-bound tools dispatch into.</param>
    /// <param name="serviceResolver">The service resolver for the current request scope.</param>
    /// <param name="httpRequestAdapter">Adapter used to read the request method, path and headers.</param>
    /// <param name="messageBodyGetter">Getter used to read the request body.</param>
    /// <param name="responseAdapter">Adapter used to write the response.</param>
    public McpHttpMiddleware(
        McpOptions options,
        IMiddlewarePipeline<BenzeneMessageContext> pipeline,
        IServiceResolver serviceResolver,
        IHttpRequestAdapter<TContext> httpRequestAdapter,
        IMessageBodyGetter<TContext> messageBodyGetter,
        IBenzeneResponseAdapter<TContext> responseAdapter)
    {
        _path = NormalizePath(options.Path);
        _serviceResolver = serviceResolver;
        _httpRequestAdapter = httpRequestAdapter;
        _messageBodyGetter = messageBodyGetter;
        _responseAdapter = responseAdapter;

        var logger = serviceResolver.TryGetService<ILoggerFactory>()?.CreateLogger("Benzene.Mcp");
        _server = new McpServer(options, pipeline, options.Log ?? (message => logger?.LogError("{Message}", message)));
    }

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "Mcp";

    /// <summary>Answers a request to the MCP path and short-circuits; any other request is passed on.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _httpRequestAdapter.Map(context);

        if (NormalizePath(request.Path) != _path)
        {
            await next();
            return;
        }

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            _responseAdapter.SetStatusCode(context, "405");
            _responseAdapter.SetResponseHeader(context, "Allow", "POST");
            _responseAdapter.SetBody(context, string.Empty);
            await _responseAdapter.FinalizeAsync(context);
            return;
        }

        var body = _messageBodyGetter.GetBody(context) ?? string.Empty;
        var sessionId = McpRequestInfo.TryParse(body)?.IsInitialize == true
            ? NewSessionId()
            : SessionIdOf(request.Headers);

        var cancellationToken = _serviceResolver.TryGetService<ICancellationTokenAccessor>()?.CancellationToken
            ?? CancellationToken.None;

        var response = await _server.HandleAsync(body, _serviceResolver, sessionId, cancellationToken);

        // A notification has no answer. 202 with nothing in it is what the protocol asks for, and the only
        // honest thing to send: there is no result, and an empty JSON object would claim there was.
        _responseAdapter.SetStatusCode(context, response is null ? "202" : "200");
        _responseAdapter.SetContentType(context, "application/json; charset=utf-8");
        if (sessionId is not null)
        {
            _responseAdapter.SetResponseHeader(context, SessionHeader, sessionId);
        }
        _responseAdapter.SetBody(context, response?.ToJsonString() ?? string.Empty);
        await _responseAdapter.FinalizeAsync(context);
    }

    /// <summary>A fresh session id: 32 lower-case hex characters.</summary>
    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The session a request says it belongs to, or <c>null</c> for a client that keeps none. Only what
    /// this server issues (32 hex characters) is accepted; anything else is not a session it knows.
    /// </summary>
    /// <param name="headers">The request headers.</param>
    public static string? SessionIdOf(IDictionary<string, string>? headers)
    {
        if (headers is null) return null;
        var value = headers.FirstOrDefault(h => string.Equals(h.Key, SessionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        return value is { Length: 32 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        if (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return trimmed.ToLowerInvariant();
    }
}
