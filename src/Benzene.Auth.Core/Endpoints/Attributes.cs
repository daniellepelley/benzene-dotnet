namespace Benzene.Auth.Core.Endpoints;

/// <summary>
/// The caller needs at least one of these scopes to reach this handler. Several
/// <c>[RequireScope]</c> attributes all apply. Enforced by <c>UseEndpointAuthorization()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequireScopeAttribute : Attribute
{
    /// <param name="anyOf">The scopes, any one of which is enough.</param>
    public RequireScopeAttribute(params string[] anyOf)
    {
        AnyOf = anyOf;
    }

    /// <summary>The scopes, any one of which is enough.</summary>
    public string[] AnyOf { get; }
}

/// <summary>
/// The caller needs at least one of these roles to reach this handler. Several
/// <c>[RequireRole]</c> attributes all apply. Enforced by <c>UseEndpointAuthorization()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequireRoleAttribute : Attribute
{
    /// <param name="anyOf">The roles, any one of which is enough.</param>
    public RequireRoleAttribute(params string[] anyOf)
    {
        AnyOf = anyOf;
    }

    /// <summary>The roles, any one of which is enough.</summary>
    public string[] AnyOf { get; }
}

/// <summary>
/// The caller must satisfy the <c>IAuthorizationPolicy</c> registered under this name
/// (<c>AddAuthorizationPolicy</c>). Several attributes all apply. Enforced by
/// <c>UseEndpointAuthorization()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequirePolicyAttribute : Attribute
{
    /// <param name="name">The policy name.</param>
    public RequirePolicyAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The policy name.</summary>
    public string Name { get; }
}

/// <summary>
/// This handler needs no caller, even where <c>UseEndpointAuthorization()</c> requires one by default.
/// It overrides every other requirement on the handler.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AllowAnonymousAttribute : Attribute
{
}
