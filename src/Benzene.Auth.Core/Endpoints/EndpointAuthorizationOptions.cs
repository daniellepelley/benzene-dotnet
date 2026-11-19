using Benzene.Abstractions;

namespace Benzene.Auth.Core.Endpoints;

/// <summary>Options for <c>UseEndpointAuthorization(...)</c>.</summary>
public class EndpointAuthorizationOptions
{
    private readonly Dictionary<string, EndpointRequirementsBuilder> _topics = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a handler with no requirement of its own needs an authenticated caller. Default <c>true</c>,
    /// so a handler that forgot its attribute fails closed. Mark open handlers <c>[AllowAnonymous]</c>.
    /// </summary>
    public bool RequireAuthenticatedByDefault { get; set; } = true;

    /// <summary>
    /// Topics that need no caller. Starts with Benzene's own reserved topics - the spec, ping, health,
    /// liveness and readiness - which have no handler class to mark. Remove one to protect it, e.g.
    /// <c>AnonymousTopics.Remove(BenzeneTopic.Spec)</c> to keep the contract document private.
    /// </summary>
    public ISet<string> AnonymousTopics { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        BenzeneTopic.Spec,
        BenzeneTopic.Ping,
        BenzeneTopic.HealthCheck,
        BenzeneTopic.Liveness,
        BenzeneTopic.Readiness
    };

    /// <summary>
    /// Requirements for one topic, declared here instead of with attributes on its handler; they add to
    /// any the handler declares. <c>options.ForTopic("tasks:create").RequireScope("tasks.write")</c>.
    /// </summary>
    public EndpointRequirementsBuilder ForTopic(string topic)
    {
        if (!_topics.TryGetValue(topic, out var builder))
        {
            builder = new EndpointRequirementsBuilder();
            _topics[topic] = builder;
        }

        return builder;
    }

    internal EndpointRequirements RequirementsFor(string? topic)
    {
        return topic != null && _topics.TryGetValue(topic, out var builder) ? builder.Build() : EndpointRequirements.None;
    }
}

/// <summary>Requirements for one topic; see <see cref="EndpointAuthorizationOptions.ForTopic"/>.</summary>
public class EndpointRequirementsBuilder
{
    private readonly List<string[]> _scopes = new();
    private readonly List<string[]> _roles = new();
    private readonly List<string> _policies = new();
    private bool _allowAnonymous;

    /// <summary>The caller needs one of these scopes.</summary>
    public EndpointRequirementsBuilder RequireScope(params string[] anyOf)
    {
        _scopes.Add(anyOf);
        return this;
    }

    /// <summary>The caller needs one of these roles.</summary>
    public EndpointRequirementsBuilder RequireRole(params string[] anyOf)
    {
        _roles.Add(anyOf);
        return this;
    }

    /// <summary>The caller must satisfy the named policy.</summary>
    public EndpointRequirementsBuilder RequirePolicy(string name)
    {
        _policies.Add(name);
        return this;
    }

    /// <summary>The topic needs no caller.</summary>
    public EndpointRequirementsBuilder AllowAnonymous()
    {
        _allowAnonymous = true;
        return this;
    }

    internal EndpointRequirements Build()
    {
        return new EndpointRequirements(_allowAnonymous, _scopes.ToArray(), _roles.ToArray(), _policies.ToArray());
    }
}
