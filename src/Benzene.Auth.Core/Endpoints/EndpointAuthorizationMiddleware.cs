using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Middleware;

namespace Benzene.Auth.Core.Endpoints;

/// <summary>
/// Authorizes each message against its handler's own requirements (<c>[RequireScope]</c>,
/// <c>[RequireRole]</c>, <c>[RequirePolicy]</c>, <c>[AllowAnonymous]</c>, or
/// <see cref="EndpointAuthorizationOptions.ForTopic"/>), before the handler is chosen and before the
/// request body is read: no caller is 401, a failed requirement 403. Place it after the authenticators
/// and before <c>UseMessageHandlers</c>.
/// </summary>
/// <remarks>
/// A topic with no handler passes through, so the router still answers <c>not-found</c>. A topic in
/// <see cref="EndpointAuthorizationOptions.AnonymousTopics"/> passes. A handler with no requirement
/// needs an authenticated caller unless <see cref="EndpointAuthorizationOptions.RequireAuthenticatedByDefault"/>
/// is off.
/// </remarks>
/// <typeparam name="TContext">The transport context type.</typeparam>
public class EndpointAuthorizationMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly EndpointAuthorizationOptions _options;
    private readonly IServiceResolver _resolver;

    /// <summary>Creates the middleware for one message's scope.</summary>
    public EndpointAuthorizationMiddleware(EndpointAuthorizationOptions options, IServiceResolver resolver)
    {
        _options = options;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public string Name => "EndpointAuthorization";

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var topic = _resolver.TryGetService<IMessageTopicGetter<TContext>>()?.GetTopic(context);
        var topicId = topic?.Id;
        if (topicId != null && _options.AnonymousTopics.Contains(topicId))
        {
            await next();
            return;
        }

        var definition = topic != null ? _resolver.TryGetService<IMessageHandlerDefinitionLookUp>()?.FindHandler(topic) : null;
        var configured = _options.RequirementsFor(topicId);
        if (definition == null && configured.IsEmpty)
        {
            // No handler: the router answers not-found. Authorizing first would turn a 404 into a 401.
            await next();
            return;
        }

        var requirements = definition?.HandlerType != null
            ? EndpointRequirements.For(definition.HandlerType).Merge(configured)
            : configured;

        if (requirements.AllowAnonymous || (requirements.IsEmpty && !_options.RequireAuthenticatedByDefault))
        {
            await next();
            return;
        }

        // The same test as RequireScope/RequireRole/RequirePolicy: an authenticator that set a caller
        // authenticated it.
        var principal = _resolver.GetService<AuthenticationHolder>().Principal;
        if (principal == null)
        {
            await AuthResults.UnauthorizedAsync(_resolver, context, "No authenticated caller");
            return;
        }

        var failure = await FirstFailureAsync(requirements, principal);
        if (failure != null)
        {
            await AuthResults.ForbiddenAsync(_resolver, context, failure);
            return;
        }

        await next();
    }

    private async Task<string?> FirstFailureAsync(EndpointRequirements requirements, System.Security.Claims.ClaimsPrincipal principal)
    {
        if (requirements.Scopes.Count > 0)
        {
            var granted = ScopeClaims.GetGrantedScopes(principal);
            foreach (var anyOf in requirements.Scopes)
            {
                if (!anyOf.Any(granted.Contains))
                {
                    return $"Missing required scope (any of: {string.Join(", ", anyOf)})";
                }
            }
        }

        foreach (var anyOf in requirements.Roles)
        {
            if (!RoleClaims.IsInAnyRole(principal, anyOf))
            {
                return $"Missing required role (any of: {string.Join(", ", anyOf)})";
            }
        }

        foreach (var name in requirements.Policies)
        {
            var policy = _resolver.GetServices<IAuthorizationPolicy>().FirstOrDefault(p => p.Name == name)
                ?? throw new InvalidOperationException(
                    $"No IAuthorizationPolicy named '{name}' is registered. Register one with AddAuthorizationPolicy.");
            if (!await policy.IsSatisfiedAsync(principal))
            {
                return $"Policy '{name}' not satisfied";
            }
        }

        return null;
    }
}
