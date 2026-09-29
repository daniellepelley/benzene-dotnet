using System.Text.Json.Nodes;
using Benzene.Abstractions.DI;

namespace Benzene.Mcp;

/// <summary>
/// The body of a tool the application writes itself, for the tools that are shaped like jobs
/// rather than like single handlers. It runs in the request's DI scope, so it can resolve an
/// <see cref="IMcpMessageDispatcher"/> and ask the pipeline as many questions as the job needs.
/// </summary>
/// <param name="call">The call as it arrived.</param>
/// <param name="context">The per-request DI scope and the dispatcher into the pipeline.</param>
/// <param name="cancellationToken">The transport's cancellation signal.</param>
public delegate Task<McpToolResult> McpToolInvoker(McpToolCall call, McpToolContext context, CancellationToken cancellationToken);

/// <summary>What a custom tool body has to hand: the request's DI scope and the way into the pipeline.</summary>
/// <param name="Services">The per-request DI scope.</param>
/// <param name="Dispatcher">Dispatches messages through the application's BenzeneMessage pipeline, one scope per message.</param>
public sealed record McpToolContext(IServiceResolver Services, IMcpMessageDispatcher Dispatcher);

/// <summary>
/// One tool, as the model meets it: a name, a description it decides from, a JSON schema for the
/// arguments, and what runs when it is called.
/// </summary>
/// <remarks>
/// <para>
/// A tool runs in one of two ways. A <b>topic-bound</b> tool (<see cref="Topic"/> set) is dispatched
/// through the application's BenzeneMessage pipeline: the arguments object becomes the message body,
/// the handler for the topic answers, and the result is mapped back to a tool result. Middleware,
/// validation, the mesh's feeds and the start-up checks all see it, exactly as they see the same
/// topic over HTTP or a queue. A <b>custom</b> tool (<see cref="Invoke"/> set) runs a body the
/// application wrote, which is the right shape when one tool composes several handlers.
/// </para>
/// <para>
/// Deliberately not one tool per handler by default. An application with a hundred topics should
/// not put a hundred schemas in front of a model; tools are shaped like the jobs a model is asked to
/// do, and the application decides which topics deserve one. The library standardizes the protocol
/// and the dispatch, not the tool design.
/// </para>
/// </remarks>
/// <param name="Name">The tool name: 1–64 characters of <c>A–Z a–z 0–9 _ -</c>, unique on the server.</param>
/// <param name="Description">What the tool does and when to use it, written for the model.</param>
/// <param name="InputSchema">The JSON Schema of the arguments object (see <see cref="McpSchema"/>).</param>
public sealed record McpToolDefinition(string Name, string Description, JsonObject InputSchema)
{
    /// <summary>The topic a topic-bound tool dispatches to, or <c>null</c> for a custom tool.</summary>
    public string? Topic { get; init; }

    /// <summary>The message version sent as <c>benzene-version</c> for a topic-bound tool; empty for the default.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The body of a custom tool, or <c>null</c> for a topic-bound tool.</summary>
    public McpToolInvoker? Invoke { get; init; }

    /// <summary>
    /// Whether the tool changes anything. Advertised as the inverse of <c>readOnlyHint</c> and
    /// <c>idempotentHint</c>, so a client can put the tools that write behind an approval and leave
    /// the readers alone. The honest default is <c>false</c> only for tools that answer questions.
    /// </summary>
    public bool Writes { get; init; }

    /// <summary>
    /// Whether the tool can destroy something it did not create (advertised as <c>destructiveHint</c>).
    /// Only meaningful when <see cref="Writes"/> is true; a write that only adds should leave it false.
    /// </summary>
    public bool Destructive { get; init; }

    /// <summary>A tool that dispatches the arguments object to <paramref name="topic"/> through the pipeline.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="description">What the tool does, written for the model.</param>
    /// <param name="inputSchema">The arguments schema; the request type's shape, as the handler expects it.</param>
    /// <param name="topic">The topic to dispatch to.</param>
    /// <param name="version">The message version, or empty for the default.</param>
    /// <param name="writes">Whether the tool changes anything.</param>
    public static McpToolDefinition ForTopic(string name, string description, JsonObject inputSchema, string topic,
        string version = "", bool writes = false) =>
        new(name, description, inputSchema) { Topic = topic, Version = version, Writes = writes };

    /// <summary>A tool whose body the application wrote.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="description">What the tool does, written for the model.</param>
    /// <param name="inputSchema">The arguments schema.</param>
    /// <param name="invoke">The body.</param>
    /// <param name="writes">Whether the tool changes anything.</param>
    public static McpToolDefinition Custom(string name, string description, JsonObject inputSchema, McpToolInvoker invoke,
        bool writes = false) =>
        new(name, description, inputSchema) { Invoke = invoke, Writes = writes };

    /// <summary>Whether this tool is dispatched through the pipeline rather than a custom body.</summary>
    public bool IsTopicBound => Topic is not null;
}
