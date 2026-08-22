namespace Benzene.JsonSchema;

/// <summary>
/// Resolves the JSON schema to validate the current request's body against.
/// </summary>
/// <typeparam name="TContext">The context type to resolve the topic/request from.</typeparam>
public interface IJsonSchemaProvider<TContext>
{
    /// <summary>
    /// Returns the schema to validate <paramref name="context"/>'s request body against, or
    /// <c>null</c> when no schema applies (e.g. the topic has no registered handler/request type).
    /// </summary>
    /// <param name="context">The current message context.</param>
    public Json.Schema.JsonSchema? Get(TContext context);
}