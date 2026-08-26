using System.Reflection;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.Exceptions;

namespace Benzene.Http.Routing;

/// <summary>
/// Discovers HTTP endpoints by scanning message handler classes for <see cref="HttpEndpointAttribute"/> attributes.
/// </summary>
/// <remarks>
/// This finder uses reflection to discover message handlers and examines their
/// <see cref="HttpEndpointAttribute"/> attributes to build the list of HTTP endpoints.
/// It validates that each route (method + path combination) is unique and throws an
/// exception if duplicate routes are detected.
/// </remarks>
public class ReflectionHttpEndpointFinder : IHttpEndpointFinder
{
    private readonly IMessageHandlersFinder _messageHandlersFinder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReflectionHttpEndpointFinder"/> class.
    /// </summary>
    /// <param name="messageHandlersFinder">The message handler finder used to discover handler classes.</param>
    public ReflectionHttpEndpointFinder(IMessageHandlersFinder messageHandlersFinder)
    {
        _messageHandlersFinder = messageHandlersFinder;
    }

    /// <summary>
    /// Finds and returns all HTTP endpoint definitions by scanning message handlers for <see cref="HttpEndpointAttribute"/> attributes.
    /// </summary>
    /// <returns>An array of HTTP endpoint definitions discovered via reflection.</returns>
    /// <exception cref="BenzeneException">Thrown when duplicate routes (same method and path) are detected.</exception>
    public IHttpEndpointDefinition[] FindDefinitions()
    {
        var handlers = _messageHandlersFinder.FindDefinitions()
            .SelectMany(MapHandlers)
            .ToArray();

        // Case-fold the grouping key only (not the stored Method/Path) - RouteFinder/CompiledRoutePath
        // match a route case-insensitively at runtime, so two handlers registering the "same" route
        // with differing case (e.g. "GET"/"/Users" vs "get"/"/USERS") are a real runtime collision:
        // the second becomes silently unreachable dead code unless this check catches it too.
        var duplicates = handlers
            .GroupBy(x => new { Method = x.Method.ToLowerInvariant(), Path = x.Path.ToLowerInvariant() })
            .Where(x => x.Count() > 1)
            .ToArray();

        if (duplicates.Any())
        {
            throw new BenzeneException(
                $"Route '{duplicates[0].Key.Method} - {duplicates[0].Key.Path}' has been assigned to more than one message handler, this is not permitted");
        }

        return handlers;
    }

    private static IHttpEndpointDefinition[] MapHandlers(IMessageHandlerDefinition messageHandlerDefinition)
    {
        return messageHandlerDefinition.HandlerType.GetCustomAttributes<HttpEndpointAttribute>()
            .Select(httpEndpoint => MapEndpoint(httpEndpoint, messageHandlerDefinition.Topic.Id))
            .Where(x => x != null)
            .Select(x => x!)
            .ToArray();
    }

    private static IHttpEndpointDefinition? MapEndpoint(HttpEndpointAttribute? httpEndpoint,
        string topic)
    {
        if (httpEndpoint == null)
        {
            return null;
        }

        return new HttpEndpointDefinition(httpEndpoint.Method, httpEndpoint.Url, topic);
    }
}
