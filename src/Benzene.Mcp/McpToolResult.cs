namespace Benzene.Mcp;

/// <summary>
/// What a tool says back. Text, because a model reads it, and <see cref="IsError"/> rather than a
/// thrown exception, because a refusal from the application ("that year is locked", "not found") is
/// something the model should read and act on, not a transport failure it never sees.
/// </summary>
/// <param name="Text">The text the model is shown.</param>
/// <param name="IsError">Whether the call was refused (<c>isError</c> on the wire).</param>
public sealed record McpToolResult(string Text, bool IsError = false)
{
    /// <summary>A successful answer.</summary>
    public static McpToolResult Ok(string text) => new(text);

    /// <summary>A refusal the model can read and act on.</summary>
    public static McpToolResult Refused(string why) => new(why, IsError: true);
}

/// <summary>
/// Something the caller can put right: a missing argument, an unknown tool, a date that is not one.
/// Thrown from a tool body (or from <see cref="McpArgs"/>) it becomes a refused tool result; thrown
/// while reading the request itself it becomes a JSON-RPC <c>invalid params</c> error.
/// </summary>
public sealed class McpRequestException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="McpRequestException"/> class.</summary>
    /// <param name="message">What was wrong, in a sentence the model can act on.</param>
    public McpRequestException(string message) : base(message)
    {
    }
}
