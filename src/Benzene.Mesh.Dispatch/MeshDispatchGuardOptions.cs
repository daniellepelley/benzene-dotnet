namespace Benzene.Mesh.Dispatch;

/// <summary>
/// What the dispatch endpoint requires of a caller before a real handler is invoked with their payload.
/// </summary>
/// <remarks>
/// Every default here is a bound on ONE signed-in human iterating on a test payload — the only caller
/// this endpoint exists for. They are not sized for a service-to-service caller, because a service
/// that wants to send a message has a transport and does not need the mesh to relay it.
/// </remarks>
public class MeshDispatchGuardOptions
{
    /// <summary>
    /// The CSRF header, a fixed contract shared with the mesh UI exactly as
    /// <c>X-Benzene-Refresh</c> is. A cross-site form cannot set a custom header, and a cross-origin
    /// fetch that sets one is preflighted.
    /// </summary>
    public const string DefaultHeaderName = "X-Benzene-Dispatch";

    /// <summary>Requests per minute one identity may dispatch.</summary>
    public const int DefaultMaxPerMinutePerIdentity = 10;

    /// <summary>Requests per minute all identities together may aim at one target service.</summary>
    public const int DefaultMaxPerMinutePerTarget = 30;

    /// <summary>The largest request body accepted, in bytes.</summary>
    public const int DefaultMaxRequestBytes = 131_072;

    /// <summary>The path this guard protects. Matched canonically, and by topic — see the middleware.</summary>
    public string Path { get; set; } = "/mesh/dispatch";

    /// <summary>
    /// The dispatch topic, matched through the registered route finder as a second signal so a route
    /// alias cannot slip past the path check. Null narrows matching to the path alone.
    /// </summary>
    public string? Topic { get; set; } = Extensions.DispatchTopic;

    /// <summary>The required CSRF header name. See <see cref="DefaultHeaderName"/>.</summary>
    public string HeaderName { get; set; } = DefaultHeaderName;

    /// <summary>
    /// Requests per minute per identity. 0 disables the per-identity limit — which is a deliberate
    /// operator choice, not a default.
    /// </summary>
    public int MaxPerMinutePerIdentity { get; set; } = DefaultMaxPerMinutePerIdentity;

    /// <summary>
    /// Requests per minute aimed at one target service, across every identity. This is the bound that
    /// matters for the target: ten people iterating politely still add up at the service.
    /// </summary>
    public int MaxPerMinutePerTarget { get; set; } = DefaultMaxPerMinutePerTarget;

    /// <summary>
    /// Largest accepted request body. Enforced before any JSON is parsed, so an oversized payload
    /// costs a length check rather than a deserialization.
    /// </summary>
    public int MaxRequestBytes { get; set; } = DefaultMaxRequestBytes;
}
