using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Benzene.Mcp;

/// <summary>Configures one MCP server: its tools, what it says about itself, and where it answers.</summary>
public sealed partial class McpBuilder
{
    private readonly List<McpToolDefinition> _tools = new();
    private string _path = McpOptions.DefaultPath;
    private string _name = "benzene";
    private string _version = "0.0.0";
    private string? _instructions;
    private Func<Exception, McpToolResult?>? _toolFailure;
    private Action<string>? _log;

    /// <summary>The path the Streamable HTTP endpoint answers on (default <c>/mcp</c>). Ignored over stdio.</summary>
    public McpBuilder AtPath(string path)
    {
        _path = path;
        return this;
    }

    /// <summary>What the server says about itself on <c>initialize</c>.</summary>
    public McpBuilder ServerInfo(string name, string version)
    {
        _name = name;
        _version = version;
        return this;
    }

    /// <summary>Guidance for the model on how to use this server's tools as a whole (sent on <c>initialize</c>).</summary>
    public McpBuilder Instructions(string instructions)
    {
        _instructions = instructions;
        return this;
    }

    /// <summary>Adds a tool.</summary>
    public McpBuilder Tool(McpToolDefinition tool)
    {
        _tools.Add(tool);
        return this;
    }

    /// <summary>Adds several tools, in order.</summary>
    public McpBuilder Tools(IEnumerable<McpToolDefinition> tools)
    {
        _tools.AddRange(tools);
        return this;
    }

    /// <summary>Adds a topic-bound tool: the arguments object is dispatched to <paramref name="topic"/> through the pipeline.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="description">What the tool does, written for the model.</param>
    /// <param name="topic">The topic to dispatch to.</param>
    /// <param name="inputSchema">The arguments schema (see <see cref="McpSchema"/>), or an empty object for a tool that takes nothing.</param>
    /// <param name="writes">Whether the tool changes anything.</param>
    /// <param name="version">The message version, or empty for the default.</param>
    public McpBuilder Tool(string name, string description, string topic, JsonObject? inputSchema = null, bool writes = false, string version = "")
    {
        return Tool(McpToolDefinition.ForTopic(name, description, inputSchema ?? McpSchema.Object(), topic, version, writes));
    }

    /// <summary>Adds a topic-bound tool whose arguments schema is derived from <typeparamref name="TRequest"/> (<see cref="McpSchema.For{T}"/>).</summary>
    /// <typeparam name="TRequest">The handler's request type.</typeparam>
    /// <param name="name">The tool name.</param>
    /// <param name="description">What the tool does, written for the model.</param>
    /// <param name="topic">The topic to dispatch to.</param>
    /// <param name="writes">Whether the tool changes anything.</param>
    /// <param name="version">The message version, or empty for the default.</param>
    public McpBuilder Tool<TRequest>(string name, string description, string topic, bool writes = false, string version = "")
    {
        return Tool(McpToolDefinition.ForTopic(name, description, McpSchema.For<TRequest>(), topic, version, writes));
    }

    /// <summary>Adds a custom tool whose body the application wrote.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="description">What the tool does, written for the model.</param>
    /// <param name="inputSchema">The arguments schema.</param>
    /// <param name="invoke">The body.</param>
    /// <param name="writes">Whether the tool changes anything.</param>
    public McpBuilder Tool(string name, string description, JsonObject inputSchema, McpToolInvoker invoke, bool writes = false)
    {
        return Tool(McpToolDefinition.Custom(name, description, inputSchema, invoke, writes));
    }

    /// <summary>
    /// Maps an exception thrown by a tool body to a refused tool result, or <c>null</c> when it is not a
    /// refusal. The application's own "no" (a domain exception) reaches the model as an answer; anything
    /// else stays a logged internal error.
    /// </summary>
    public McpBuilder OnToolFailure(Func<Exception, McpToolResult?> toolFailure)
    {
        _toolFailure = toolFailure;
        return this;
    }

    /// <summary>Where unexpected failures are written. Defaults to Benzene's logger over HTTP and standard error over stdio.</summary>
    public McpBuilder Log(Action<string> log)
    {
        _log = log;
        return this;
    }

    /// <summary>Validates and builds the options. Throws <see cref="ArgumentException"/> naming the first tool that is not usable.</summary>
    public McpOptions Build()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in _tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !ToolName().IsMatch(tool.Name))
                throw new ArgumentException($"MCP tool name '{tool.Name}' is not valid: use 1-64 characters of A-Z, a-z, 0-9, '_' or '-'.");
            if (!seen.Add(tool.Name))
                throw new ArgumentException($"MCP tool '{tool.Name}' is registered twice; tool names must be unique.");
            if (string.IsNullOrWhiteSpace(tool.Description))
                throw new ArgumentException($"MCP tool '{tool.Name}' has no description; the model decides from it, so it is required.");
            if (tool.Invoke is null && string.IsNullOrWhiteSpace(tool.Topic))
                throw new ArgumentException($"MCP tool '{tool.Name}' has neither a topic nor a body: set Topic (dispatch through the pipeline) or Invoke (a custom body).");
            if (tool.Invoke is not null && tool.Topic is not null)
                throw new ArgumentException($"MCP tool '{tool.Name}' has both a topic and a body; a tool is one or the other.");
        }

        return new McpOptions(_path, new McpServerInfo(_name, _version, _instructions), _tools.ToArray(), _toolFailure, _log);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ToolName();
}
