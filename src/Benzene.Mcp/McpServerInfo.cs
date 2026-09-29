namespace Benzene.Mcp;

/// <summary>What the server says about itself on <c>initialize</c>.</summary>
/// <param name="Name">The server name a client shows.</param>
/// <param name="Version">The server version.</param>
/// <param name="Instructions">Optional guidance for the model on how to use this server's tools as a whole.</param>
public sealed record McpServerInfo(string Name, string Version, string? Instructions = null);
