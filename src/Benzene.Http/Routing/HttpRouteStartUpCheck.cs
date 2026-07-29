using Benzene.Abstractions.DI;
using Benzene.Abstractions.StartUpChecks;

namespace Benzene.Http.Routing;

/// <summary>
/// Forces the route table to be built at start-up rather than on the first request.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UnroutedHttpEndpointCheck"/> already produces an excellent message for a handler with
/// <c>[HttpEndpoint]</c> and no <c>[Message]</c> — but it is a finder, so it only runs when the route
/// table is first compiled, and that is the first request. Resolving <see cref="IRouteFinder"/> here
/// compiles the table at INIT instead, which is the whole of the change: the same error, hours
/// earlier, and off the request path.
/// </para>
/// <para>
/// Doing the work here also means the first real request no longer pays for it.
/// </para>
/// </remarks>
public class HttpRouteStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "http-routes";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        // Resolving is the check: RouteFinder compiles the table in its constructor, and the finders it
        // composes throw from there if something is unroutable.
        resolver.TryGetService<IRouteFinder>();
    }
}
