using System.Collections.Generic;
using Benzene.Http.Routing;
using Benzene.Mesh.Artifacts;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// <see cref="MeshPathCanonicalizer.IsPathOrTopicMatch"/> - the shared path-OR-topic predicate
/// <see cref="MeshDispatchGuardMiddleware{TContext}.HandleAsync"/>'s <c>IsGuarded</c> check uses, and
/// that <c>MeshAuthGate</c>'s <c>dispatchRole</c> check (a different assembly,
/// <c>deploy/Mesh/Benzene.Mesh.Host</c>) now also calls into (#287), so the two gates can never
/// disagree on what counts as the guarded endpoint.
/// </summary>
public class MeshPathCanonicalizerTest
{
    private const string GuardedPath = "/mesh/dispatch";
    private const string Topic = "benzene:mesh:dispatch";

    private static readonly string GuardedCanonicalPath = MeshPathCanonicalizer.Canonicalize(GuardedPath);

    [Fact]
    public void ExactPathMatch_IsGuarded_EvenWithNoRouteFinder()
    {
        Assert.True(MeshPathCanonicalizer.IsPathOrTopicMatch(
            "POST", GuardedPath, GuardedCanonicalPath, Topic, routeFinder: null));
    }

    [Theory]
    [InlineData("/MESH/DISPATCH")]
    [InlineData("//mesh//dispatch")]
    [InlineData("/mesh/dispatch?x=1")]
    public void OddSpellingOfTheGuardedPath_IsStillGuarded(string path)
    {
        Assert.True(MeshPathCanonicalizer.IsPathOrTopicMatch(
            "POST", path, GuardedCanonicalPath, Topic, routeFinder: null));
    }

    [Fact]
    public void UnrelatedPath_WithNoRouteFinder_IsNotGuarded()
    {
        Assert.False(MeshPathCanonicalizer.IsPathOrTopicMatch(
            "GET", "/mesh-ui", GuardedCanonicalPath, Topic, routeFinder: null));
    }

    /// <summary>
    /// The case #287 exists for: a route alias reaching a DIFFERENT literal path than
    /// <see cref="GuardedPath"/>, which the route finder nonetheless resolves to the guarded topic.
    /// </summary>
    [Fact]
    public void RouteAliasResolvingToTheGuardedTopic_IsGuarded()
    {
        var routeFinder = new Mock<IRouteFinder>();
        routeFinder.Setup(x => x.Find("POST", "/v2/mesh/dispatch"))
            .Returns(new HttpTopicRoute(Topic, new Dictionary<string, object>()));

        Assert.True(MeshPathCanonicalizer.IsPathOrTopicMatch(
            "POST", "/v2/mesh/dispatch", GuardedCanonicalPath, Topic, routeFinder.Object));
    }

    [Fact]
    public void RouteResolvingToSomeOtherTopic_IsNotGuarded()
    {
        var routeFinder = new Mock<IRouteFinder>();
        routeFinder.Setup(x => x.Find(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HttpTopicRoute("orders:create", new Dictionary<string, object>()));

        Assert.False(MeshPathCanonicalizer.IsPathOrTopicMatch(
            "POST", "/orders", GuardedCanonicalPath, Topic, routeFinder.Object));
    }

    [Fact]
    public void NoTopicConfigured_FallsBackToPathOnly_EvenWithARouteFinder()
    {
        var routeFinder = new Mock<IRouteFinder>();
        routeFinder.Setup(x => x.Find(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HttpTopicRoute(Topic, new Dictionary<string, object>()));

        Assert.False(MeshPathCanonicalizer.IsPathOrTopicMatch(
            "POST", "/v2/mesh/dispatch", GuardedCanonicalPath, topic: null, routeFinder.Object));
    }
}
