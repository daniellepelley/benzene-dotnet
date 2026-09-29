namespace Benzene.Mcp;

/// <summary>The built configuration of one MCP server: its tools, identity and hooks. Produced by <see cref="McpBuilder"/>.</summary>
public sealed class McpOptions
{
    /// <summary>The path the Streamable HTTP endpoint answers on. Ignored by the stdio host.</summary>
    public const string DefaultPath = "/mcp";

    internal McpOptions(string path, McpServerInfo serverInfo, IReadOnlyList<McpToolDefinition> tools,
        Func<Exception, McpToolResult?>? toolFailure, Action<string>? log)
    {
        Path = path;
        ServerInfo = serverInfo;
        Tools = tools;
        ToolFailure = toolFailure;
        Log = log;
    }

    /// <summary>The HTTP path (default <see cref="DefaultPath"/>).</summary>
    public string Path { get; }

    /// <summary>What the server says about itself.</summary>
    public McpServerInfo ServerInfo { get; }

    /// <summary>The tools, in the order they are advertised.</summary>
    public IReadOnlyList<McpToolDefinition> Tools { get; }

    /// <summary>
    /// Turns an exception thrown by a tool body into a refused tool result, or returns <c>null</c>
    /// for an exception that is not a refusal. Lets the application's own "no" (a domain exception)
    /// reach the model as an answer it can act on, while anything unexpected stays a logged internal
    /// error the model is never shown the detail of.
    /// </summary>
    public Func<Exception, McpToolResult?>? ToolFailure { get; }

    /// <summary>Where unexpected failures are written. Defaults to Benzene's logger on HTTP, and standard error on stdio.</summary>
    public Action<string>? Log { get; }
}
