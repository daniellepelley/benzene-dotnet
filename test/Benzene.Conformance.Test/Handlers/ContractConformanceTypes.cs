using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.MessageHandlers;

namespace Benzene.Conformance.Test.Handlers;

// The canonical payload types of conformance/README.md ("Canonical payload types"). Every property is
// always emitted - Benzene's default System.Text.Json serializer writes nulls - so every property is
// required (contract-document.md §4.2).

public class Greeted
{
    public string Greeting { get; set; } = string.Empty;
}

public class LogEntry
{
    public string Message { get; set; } = string.Empty;
}

public class LookupRequest
{
    public string Key { get; set; } = string.Empty;
}

public class LookupResponse
{
    public string Value { get; set; } = string.Empty;
}

public class ShapesChild
{
    public string Name { get; set; } = string.Empty;
}

public class ShapesRequest
{
    public int Count { get; set; }
    public double Ratio { get; set; }
    public bool Active { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> Labels { get; set; } = new();
    public string? Note { get; set; }
    public ShapesChild Child { get; set; } = new();
    public ShapesChild? NullableChild { get; set; }
}

public class TreeNode
{
    public string Name { get; set; } = string.Empty;
    public TreeNode? Parent { get; set; }
    public TreeNode[] Children { get; set; } = Array.Empty<TreeNode>();
}

/// <summary><c>conformance:shapes</c> - a handler that returns no payload.</summary>
[Message("conformance:shapes")]
public class ShapesConformanceHandler : IMessageHandler<ShapesRequest>
{
    public Task HandleAsync(ShapesRequest request) => Task.CompletedTask;
}

/// <summary><c>conformance:tree</c> - a recursive request, no payload returned.</summary>
[Message("conformance:tree")]
public class TreeConformanceHandler : IMessageHandler<TreeNode>
{
    public Task HandleAsync(TreeNode request) => Task.CompletedTask;
}
