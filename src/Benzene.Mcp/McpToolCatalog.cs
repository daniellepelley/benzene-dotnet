namespace Benzene.Mcp;

/// <summary>
/// The tools an MCP server advertises, registered as a singleton so middleware ahead of the endpoint
/// (an authorizer deciding which scope a call needs, say) can read the same definitions the tool list
/// publishes rather than keeping a second list of which tools write.
/// </summary>
/// <param name="Tools">The tool definitions, in advertised order.</param>
public sealed record McpToolCatalog(IReadOnlyList<McpToolDefinition> Tools)
{
    /// <summary>The tool with the given name, or <c>null</c>.</summary>
    /// <param name="name">The tool name.</param>
    public McpToolDefinition? Find(string? name) =>
        name is null ? null : Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}
