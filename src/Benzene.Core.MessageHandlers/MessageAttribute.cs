namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Marks a message handler class with the topic (and optionally version) it handles, so
/// <see cref="ReflectionMessageHandlersFinder"/> can discover it and route messages to it.
/// </summary>
/// <remarks>
/// Apply this to a class implementing <c>IMessageHandler&lt;TRequest&gt;</c> or
/// <c>IMessageHandler&lt;TRequest, TResponse&gt;</c>. Only one <see cref="MessageAttribute"/> per
/// class is used; a class without this attribute is not discovered by reflection-based handler
/// discovery. Two handlers may not share the same topic and version - <see cref="ReflectionMessageHandlersFinder"/>
/// throws if it detects that at discovery time.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class MessageAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageAttribute"/> class.
    /// </summary>
    /// <param name="topic">The topic id this handler processes messages for.</param>
    /// <param name="version">
    /// The optional version of this handler: a message carrying exactly this version (its
    /// <c>benzene-version</c> header) is routed here by <see cref="Benzene.Abstractions.MessageHandlers.IVersionSelector"/>.
    /// Defaults to an empty string (unversioned) - the handler a message with no version goes to.
    /// </param>
    public MessageAttribute(string topic, string version = "")
    {
        Version = version;
        Topic = topic;
    }

    /// <summary>
    /// Gets the version of this handler, as supplied to the constructor.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the topic id this handler processes messages for.
    /// </summary>
    public string Topic { get; }

    /// <summary>
    /// Declares this versioned handler the topic's <b>default</b>: a message that carries no version is
    /// routed here when the topic has no unversioned handler (docs/specification/core-concepts.md §2).
    /// <c>[Message("order:create", "v1", IsDefault = true)]</c>. At most one handler per topic is the
    /// default - an unversioned handler always is one - and a topic whose handlers are all versioned
    /// with none declared default is reported at start-up, since every unversioned message to it fails
    /// with <c>not-found</c>.
    /// </summary>
    public bool IsDefault { get; set; }
}
