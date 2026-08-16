using System;
using Benzene.Core.MessageHandlers;
using Benzene.Http.Routing;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Pins the routing-layer guarantee the mesh's refresh endpoint leans on: a handler declared
/// <c>[HttpEndpoint("POST", "/mesh/refresh")]</c> is reachable by POST and by nothing else. This matters
/// because <c>SameSite=Lax</c> <em>does</em> send the session cookie on a top-level GET navigation, so a
/// GET-triggerable refresh would be trivially CSRF-able from an <c>&lt;img&gt;</c> tag or a plain link -
/// the route table's method scoping is what makes that impossible, and it should stay a tested property
/// rather than an assumed one.
/// <para>
/// It also pins the second half of that contract: the exact set of path spellings the router still
/// accepts for the same route. <see cref="MeshRefreshGuardMiddleware{TContext}"/> re-implements that
/// normalization to decide what to guard, so the two must agree - a spelling the router accepts but the
/// guard doesn't recognise would be a straight bypass.
/// </para>
/// </summary>
public class MeshRefreshRoutingTest
{
    // Declared exactly as examples/AwsMesh/Mesh/MeshAggregateHandler is (same topic, same single POST
    // endpoint), and deliberately SHARED with AwsMeshRefreshEndpointTest rather than duplicated: this
    // assembly may contain only one handler for a given topic, because the sibling
    // AwsMeshFleetEndpointTest scans the whole assembly and a second one would trip
    // ReflectionMessageHandlersFinder's duplicate-topic check for every test in the class.
    private static Type HandlerType => typeof(AwsMeshRefreshEndpointTest.SpyAggregateHandler);

    // Built directly over the one candidate type rather than through DI: no container, no assembly
    // scan, so this test's fixture cannot leak into another's handler registry.
    private static RouteFinder CreateRouteFinder()
        => new(new ReflectionHttpEndpointFinder(new ReflectionMessageHandlersFinder(new[] { HandlerType })));

    [Fact]
    public void RouteTable_ExposesExactlyOneRefreshEndpoint_AndItIsPost()
    {
        var definitions = new ReflectionHttpEndpointFinder(
            new ReflectionMessageHandlersFinder(new[] { HandlerType })).FindDefinitions();

        var definition = Assert.Single(definitions);
        Assert.Equal("POST", definition.Method);
        Assert.Equal("/mesh/refresh", definition.Path);
        Assert.Equal(MeshAggregatorTopics.Aggregate, definition.Topic);
    }

    [Fact]
    public void Find_Post_ResolvesTheAggregateTopic()
    {
        var route = CreateRouteFinder().Find("POST", "/mesh/refresh");

        Assert.NotNull(route);
        Assert.Equal(MeshAggregatorTopics.Aggregate, route!.Topic);
    }

    /// <summary>
    /// The one that actually matters for CSRF: no method other than POST reaches the handler, so no
    /// cross-site navigation, image, link, prefetch, or preflight can trigger a pass by routing alone.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Find_AnyMethodOtherThanPost_ResolvesNoRoute(string method)
    {
        Assert.Null(CreateRouteFinder().Find(method, "/mesh/refresh"));
    }

    /// <summary>
    /// Every spelling here reaches the handler, so every one of them must also be recognised by the
    /// guard - which is asserted directly below, against the guard's own normalization.
    /// </summary>
    [Theory]
    [InlineData("/mesh/refresh")]
    [InlineData("/mesh/refresh/")]
    [InlineData("//mesh//refresh")]
    [InlineData("/mesh/refresh//")]
    [InlineData("/MESH/REFRESH")]
    [InlineData("/Mesh/Refresh")]
    [InlineData("/mesh/refresh?force=1")]
    [InlineData("/mesh/refresh/?x=1")]
    public void Find_PathSpellingVariants_AllResolveTheSameRoute(string path)
    {
        var route = CreateRouteFinder().Find("POST", path);

        Assert.NotNull(route);
        Assert.Equal(MeshAggregatorTopics.Aggregate, route!.Topic);
    }

    /// <summary>
    /// The equivalence itself: for the same corpus of paths, "the router routes it" and "the guard
    /// guards it" agree in both directions. This is what makes the guard's cheap string match a safe
    /// substitute for consulting the route table on every request.
    /// </summary>
    [Theory]
    // Routed → must be guarded.
    [InlineData("/mesh/refresh", true)]
    [InlineData("/mesh/refresh/", true)]
    [InlineData("//mesh//refresh", true)]
    [InlineData("/MESH/REFRESH", true)]
    [InlineData("/mesh/refresh?force=1", true)]
    // Not routed → must not be guarded (the guard must not swallow unrelated traffic).
    [InlineData("/mesh/refreshx", false)]
    [InlineData("/mesh", false)]
    [InlineData("/refresh", false)]
    [InlineData("/mesh/refresh/extra", false)]
    [InlineData("/mesh-ui", false)]
    [InlineData("/", false)]
    public void GuardNormalization_AgreesWithTheRouter(string path, bool expected)
    {
        var routed = CreateRouteFinder().Find("POST", path) != null;
        var guarded = MeshRefreshGuardMiddleware<MeshArtifactMiddlewareTest.FakeHttpContext>
            .Canonicalize(path) == MeshRefreshGuardOptions.DefaultPath;

        Assert.Equal(expected, routed);
        Assert.Equal(routed, guarded);
    }
}
