using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.Exceptions;
using Benzene.Core.Messages;

namespace Benzene.Mcp;

/// <summary>
/// Every topic-bound MCP tool names a topic some registered handler answers. A tool whose topic
/// nobody handles would advertise itself to a model and then answer <c>not-found</c> on every call;
/// this reports it at start-up instead, naming the tool, the topic, and what to add.
/// </summary>
public sealed class McpToolTopicStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "mcp-tool-topics";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var tools = resolver.GetServices<McpToolCatalog>()
            .SelectMany(c => c.Tools)
            .Where(t => t.IsTopicBound)
            .ToArray();
        if (tools.Length == 0)
        {
            return;
        }

        var lookUp = resolver.TryGetService<IMessageHandlerDefinitionLookUp>();
        if (lookUp is null)
        {
            // No handler registry at all: nothing to check against. The pipeline itself will say not-found.
            return;
        }

        var missing = tools
            .Where(t => lookUp.FindHandler(new Topic(t.Topic!, t.Version)) is null)
            .Select(t => (t.Name, t.Topic!, t.Version))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new McpToolWithoutHandlerException(missing);
        }
    }
}

/// <summary>Thrown by <see cref="McpToolTopicStartUpCheck"/> when a topic-bound MCP tool names a topic with no handler.</summary>
public sealed class McpToolWithoutHandlerException : BenzeneException
{
    /// <summary>Initializes a new instance of the <see cref="McpToolWithoutHandlerException"/> class.</summary>
    /// <param name="missing">The tools whose topics have no handler.</param>
    public McpToolWithoutHandlerException(IReadOnlyList<(string Tool, string Topic, string Version)> missing)
        : base(Describe(missing))
    {
        Missing = missing;
    }

    /// <summary>The tools whose topics have no handler.</summary>
    public IReadOnlyList<(string Tool, string Topic, string Version)> Missing { get; }

    private static string Describe(IReadOnlyList<(string Tool, string Topic, string Version)> missing)
    {
        var lines = missing.Select(m => string.IsNullOrEmpty(m.Version)
            ? $"  tool '{m.Tool}' -> topic '{m.Topic}'"
            : $"  tool '{m.Tool}' -> topic '{m.Topic}' version '{m.Version}'");
        return "These MCP tools name a topic no registered message handler answers, so every call would return not-found:\n"
               + string.Join("\n", lines)
               + "\nRegister a handler for each topic in the pipeline passed to UseMcp/UseMcpStdio (for example, via "
               + "UseMessageHandlers(...) with a handler marked [Message(\"<topic>\")]), or fix the tool's topic.";
    }
}
