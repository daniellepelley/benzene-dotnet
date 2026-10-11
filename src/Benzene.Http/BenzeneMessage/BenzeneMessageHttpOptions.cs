namespace Benzene.Http.BenzeneMessage;

/// <summary>
/// Options for the BenzeneMessage-over-HTTP endpoint (see
/// <see cref="Extensions.UseBenzeneMessage{TContext}(Benzene.Abstractions.Middleware.IMiddlewarePipelineBuilder{TContext}, System.Action{Benzene.Abstractions.Middleware.IMiddlewarePipelineBuilder{Benzene.Core.Messages.BenzeneMessage.BenzeneMessageContext}})"/>).
/// </summary>
public class BenzeneMessageHttpOptions
{
    /// <summary>The default path the endpoint listens on.</summary>
    public const string DefaultPath = "/benzene-message";

    /// <summary>
    /// Gets or sets the path the endpoint listens on for POSTed BenzeneMessage envelopes.
    /// Defaults to <see cref="DefaultPath"/>.
    /// </summary>
    public string Path { get; set; } = DefaultPath;

    /// <summary>
    /// Gets or sets an optional topic allowlist predicate - the restriction the spec requires the envelope
    /// endpoint to support (transport-bindings.md §1, D21), since the endpoint reaches every handler the
    /// service registers, including topics with no HTTP route of their own. An envelope whose topic the
    /// predicate rejects is answered <c>not-found</c> exactly as if no handler matched (HTTP 200, a
    /// <c>not-found</c> envelope carrying the router's problem document) instead of being dispatched.
    /// When null (the default), every topic is dispatched.
    /// </summary>
    /// <example><c>TopicFilter = topic =&gt; topic.StartsWith("orders:") || topic == BenzeneTopic.HealthCheck</c></example>
    public Func<string, bool>? TopicFilter { get; set; }
}
