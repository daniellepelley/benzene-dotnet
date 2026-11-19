using System.Collections.Concurrent;
using System.Reflection;

namespace Benzene.Auth.Core.Endpoints;

/// <summary>
/// What one endpoint needs: whether it is anonymous, and its scope, role and policy requirements. Every
/// requirement applies (AND); within one scope or role requirement, any of its values is enough (OR).
/// </summary>
public sealed class EndpointRequirements
{
    private static readonly ConcurrentDictionary<Type, EndpointRequirements> ByHandlerType = new();

    /// <summary>No requirement of its own.</summary>
    public static readonly EndpointRequirements None = new(false, Array.Empty<string[]>(), Array.Empty<string[]>(), Array.Empty<string>());

    /// <summary>Creates a set of requirements.</summary>
    public EndpointRequirements(bool allowAnonymous, IReadOnlyList<string[]> scopes, IReadOnlyList<string[]> roles, IReadOnlyList<string> policies)
    {
        AllowAnonymous = allowAnonymous;
        Scopes = scopes;
        Roles = roles;
        Policies = policies;
    }

    /// <summary>Whether the endpoint needs no caller.</summary>
    public bool AllowAnonymous { get; }

    /// <summary>Scope requirements: each needs one of its scopes.</summary>
    public IReadOnlyList<string[]> Scopes { get; }

    /// <summary>Role requirements: each needs one of its roles.</summary>
    public IReadOnlyList<string[]> Roles { get; }

    /// <summary>Named policies, each of which must be satisfied.</summary>
    public IReadOnlyList<string> Policies { get; }

    /// <summary>Whether the endpoint declares anything at all.</summary>
    public bool IsEmpty => !AllowAnonymous && Scopes.Count == 0 && Roles.Count == 0 && Policies.Count == 0;

    /// <summary>The requirements a handler type declares through attributes, read once per type.</summary>
    public static EndpointRequirements For(Type handlerType)
    {
        return ByHandlerType.GetOrAdd(handlerType, static type => new EndpointRequirements(
            type.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) != null,
            type.GetCustomAttributes<RequireScopeAttribute>(inherit: true).Select(a => a.AnyOf).ToArray(),
            type.GetCustomAttributes<RequireRoleAttribute>(inherit: true).Select(a => a.AnyOf).ToArray(),
            type.GetCustomAttributes<RequirePolicyAttribute>(inherit: true).Select(a => a.Name).ToArray()));
    }

    /// <summary>Both sets of requirements together.</summary>
    public EndpointRequirements Merge(EndpointRequirements other)
    {
        if (other.IsEmpty)
        {
            return this;
        }

        return IsEmpty
            ? other
            : new EndpointRequirements(
                AllowAnonymous || other.AllowAnonymous,
                Scopes.Concat(other.Scopes).ToArray(),
                Roles.Concat(other.Roles).ToArray(),
                Policies.Concat(other.Policies).ToArray());
    }
}
