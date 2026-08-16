using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Http;
using Benzene.Http.Routing;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// The CSRF + throttle guard in front of the mesh's refresh endpoint. These are adversarial tests as
/// much as functional ones: several of them exist specifically to pin that a crafted request cannot
/// slip past the guard while still reaching the handler (odd path spellings, header-name casing,
/// unexpected methods), and that a denial does no work and says nothing useful.
/// </summary>
public class MeshRefreshGuardMiddlewareTest
{
    public class FakeHttpContext : IHttpContext
    {
    }

    /// <summary>
    /// Hand-written rather than mocked so a test can assert on <em>whether the store was touched at
    /// all</em> - the load-bearing evidence for "the header check runs before any I/O".
    /// </summary>
    private sealed class FakeMeshArtifactStore : IMeshArtifactStore
    {
        private readonly string? _manifest;
        private readonly Exception? _throwOnRead;

        public FakeMeshArtifactStore(string? manifest = null, Exception? throwOnRead = null)
        {
            _manifest = manifest;
            _throwOnRead = throwOnRead;
        }

        public List<string> ReadKeys { get; } = new();

        public Task PublishAsync(string relativePath, string content) =>
            throw new NotSupportedException("The middleware under test never publishes.");

        public Task<string?> TryReadAsync(string relativePath)
        {
            ReadKeys.Add(relativePath);
            if (_throwOnRead != null)
            {
                throw _throwOnRead;
            }

            return Task.FromResult(_manifest);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static string ManifestGeneratedAt(DateTimeOffset at)
        => "{\"generatedAtUtc\":\"" + at.ToString("O") + "\",\"services\":[]}";

    private sealed class Harness
    {
        public FakeHttpContext Context { get; } = new();
        public Mock<IBenzeneResponseAdapter<FakeHttpContext>> Response { get; } = new();
        public FakeMeshArtifactStore Store { get; }
        public bool NextCalled { get; private set; }

        private readonly MeshRefreshGuardMiddleware<FakeHttpContext> _middleware;

        public Harness(
            string method, string path, IDictionary<string, string>? headers = null,
            FakeMeshArtifactStore? store = null, MeshRefreshGuardOptions? options = null,
            IRouteFinder? routeFinder = null)
        {
            Store = store ?? new FakeMeshArtifactStore();

            var request = new Mock<IHttpRequestAdapter<FakeHttpContext>>();
            request.Setup(x => x.Map(Context)).Returns(new HttpRequest
            {
                Method = method,
                Path = path,
                Headers = headers ?? new Dictionary<string, string>(),
            });

            _middleware = new MeshRefreshGuardMiddleware<FakeHttpContext>(
                options ?? new MeshRefreshGuardOptions(), Store, request.Object, Response.Object,
                routeFinder, logger: null, clock: () => Now);
        }

        public Task RunAsync() => _middleware.HandleAsync(Context, () =>
        {
            NextCalled = true;
            return Task.CompletedTask;
        });

        public void AssertAllowed()
        {
            Assert.True(NextCalled);
            Response.Verify(x => x.SetStatusCode(Context, It.IsAny<string>()), Times.Never);
            Response.Verify(x => x.FinalizeAsync(Context), Times.Never);
        }

        public void AssertDenied(string statusCode, string body)
        {
            Assert.False(NextCalled);
            Response.Verify(x => x.SetStatusCode(Context, statusCode), Times.Once);
            Response.Verify(x => x.SetContentType(Context, "application/json"), Times.Once);
            Response.Verify(x => x.SetBody(Context, body), Times.Once);
            Response.Verify(x => x.FinalizeAsync(Context), Times.Once);
        }
    }

    private static Dictionary<string, string> WithRefreshHeader(string name = "X-Benzene-Refresh", string value = "1")
        => new(StringComparer.Ordinal) { [name] = value };

    // ---------------------------------------------------------------------------------------------
    // Pass-through: everything that is not the refresh endpoint must be untouched.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/mesh-ui")]
    [InlineData("GET", "/manifest.json")]
    [InlineData("POST", "/benzene/invoke")]
    [InlineData("POST", "/mesh/refreshx")]
    [InlineData("POST", "/mesh")]
    [InlineData("POST", "/refresh")]
    public async Task HandleAsync_UnrelatedRequest_FallsThroughWithoutTouchingTheStore(string method, string path)
    {
        var harness = new Harness(method, path);

        await harness.RunAsync();

        harness.AssertAllowed();
        Assert.Empty(harness.Store.ReadKeys);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 1: the custom header (CSRF).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_RefreshWithoutHeader_Is403AndDoesNoWork()
    {
        // The whole point of the header check running first: a caller who could not set a custom
        // header triggers no aggregation AND no store read - so there is no timing or state-dependent
        // signal for them to read off the response either.
        var harness = new Harness("POST", "/mesh/refresh",
            store: new FakeMeshArtifactStore(ManifestGeneratedAt(Now)));

        await harness.RunAsync();

        harness.AssertDenied("403", "{\"error\":\"forbidden\"}");
        Assert.Empty(harness.Store.ReadKeys);
    }

    [Fact]
    public async Task HandleAsync_RefreshWithHeader_IsAllowedThrough()
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader());

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    /// <summary>
    /// HTTP header names are case-insensitive, and both API Gateway payload formats and the various
    /// browsers normalize them differently - so a differently-cased header must NOT be a way to make
    /// the guard reject a legitimate UI request (nor, conversely, a way to satisfy it accidentally).
    /// </summary>
    [Theory]
    [InlineData("X-Benzene-Refresh")]
    [InlineData("x-benzene-refresh")]
    [InlineData("X-BENZENE-REFRESH")]
    [InlineData("x-BeNzEnE-rEfReSh")]
    public async Task HandleAsync_HeaderNameCasing_IsIgnored(string headerName)
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(headerName));

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    /// <summary>
    /// The header's <em>value</em> is deliberately not inspected: what defends against CSRF is that a
    /// cross-site caller cannot set a custom header at all. Pinning this keeps a future "must equal 1"
    /// tightening from being mistaken for a security improvement - it would only break UI variants.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("anything")]
    public async Task HandleAsync_HeaderValue_IsNotInspected(string value)
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(value: value));

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_EmptyHeaderValue_IsTreatedAsAbsent(string value)
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(value: value));

        await harness.RunAsync();

        harness.AssertDenied("403", "{\"error\":\"forbidden\"}");
    }

    /// <summary>
    /// Every spelling of the path that <c>Benzene.Http.Routing.RouteFinder</c> would still route to the
    /// handler must also be seen by the guard. <see cref="MeshRefreshRoutingTest"/> proves the router
    /// really does route all of these; this proves the guard matches the same set.
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
    public async Task HandleAsync_PathSpellingVariants_AreAllGuarded(string path)
    {
        var harness = new Harness("POST", path);

        await harness.RunAsync();

        harness.AssertDenied("403", "{\"error\":\"forbidden\"}");
    }

    /// <summary>
    /// Matching is not restricted to POST. The router maps POST only today (see
    /// <see cref="MeshRefreshRoutingTest"/>), so these methods 404 downstream anyway - but guarding
    /// them means adding a GET/PUT alias later cannot silently open a CSRF hole, and a top-level GET
    /// navigation (the one cross-site request <c>SameSite=Lax</c> still sends cookies on) is refused
    /// here rather than relying on the route table's shape.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task HandleAsync_NonPostMethodOnRefreshPath_StillRequiresTheHeader(string method)
    {
        var harness = new Harness(method, "/mesh/refresh");

        await harness.RunAsync();

        harness.AssertDenied("403", "{\"error\":\"forbidden\"}");
    }

    /// <summary>
    /// A route alias the guard's own path match cannot see - a second <c>[HttpEndpoint]</c> on the same
    /// handler, or the version-prefixed alias <c>AddHttpVersioning()</c> synthesises - still resolves to
    /// the guarded topic, and the route finder is what catches it.
    /// </summary>
    [Fact]
    public async Task HandleAsync_RouteAliasResolvingToTheGuardedTopic_IsGuarded()
    {
        var routeFinder = new Mock<IRouteFinder>();
        routeFinder.Setup(x => x.Find("POST", "/v2/mesh/refresh"))
            .Returns(new HttpTopicRoute(MeshAggregatorTopics.Aggregate, new Dictionary<string, object>()));

        var harness = new Harness("POST", "/v2/mesh/refresh", routeFinder: routeFinder.Object);

        await harness.RunAsync();

        harness.AssertDenied("403", "{\"error\":\"forbidden\"}");
    }

    [Fact]
    public async Task HandleAsync_RouteResolvingToSomeOtherTopic_IsNotGuarded()
    {
        var routeFinder = new Mock<IRouteFinder>();
        routeFinder.Setup(x => x.Find(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HttpTopicRoute("orders:create", new Dictionary<string, object>()));

        var harness = new Harness("POST", "/orders", routeFinder: routeFinder.Object);

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2: the throttle.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_LastPassInsideTheWindow_Is429WithRetryAfter()
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(ManifestGeneratedAt(Now.AddSeconds(-10))));

        await harness.RunAsync();

        harness.AssertDenied("429", "{\"error\":\"throttled\"}");
        Assert.Equal(new[] { "manifest.json" }, harness.Store.ReadKeys);
        harness.Response.Verify(x => x.SetResponseHeader(harness.Context, "Retry-After", "20"), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_LastPassOutsideTheWindow_IsAllowedThrough()
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(ManifestGeneratedAt(Now.AddSeconds(-31))));

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    [Fact]
    public async Task HandleAsync_LastPassExactlyOnTheWindow_IsAllowedThrough()
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(ManifestGeneratedAt(Now - MeshRefreshGuardOptions.DefaultMinimumInterval)));

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    /// <summary>
    /// A manifest timestamped in the future (clock skew, or a hand-edited artifact) must throttle, not
    /// grant an unbounded allowance - the safe direction is declining work.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ManifestTimestampInTheFuture_Throttles()
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(ManifestGeneratedAt(Now.AddHours(1))));

        await harness.RunAsync();

        harness.AssertDenied("429", "{\"error\":\"throttled\"}");
    }

    /// <summary>
    /// Fails open, deliberately: the first refresh after a deploy has no manifest to read (and that
    /// first pass is exactly what CI triggers), so a missing/corrupt/unreadable manifest must allow the
    /// pass rather than brick a fresh deployment. See the middleware's remarks for the security cost.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"generatedAtUtc\":null}")]
    [InlineData("{\"generatedAtUtc\":\"not-a-date\"}")]
    [InlineData("[]")]
    public async Task HandleAsync_UnusableManifest_FailsOpen(string? manifest)
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(manifest));

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    [Fact]
    public async Task HandleAsync_StoreThrows_FailsOpenRatherThanErroring()
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(throwOnRead: new InvalidOperationException("S3 is having a day")));

        await harness.RunAsync();

        harness.AssertAllowed();
    }

    [Fact]
    public async Task HandleAsync_ZeroMinimumInterval_DisablesTheThrottleWithoutReadingTheStore()
    {
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(ManifestGeneratedAt(Now)),
            options: new MeshRefreshGuardOptions { MinimumInterval = TimeSpan.Zero });

        await harness.RunAsync();

        harness.AssertAllowed();
        Assert.Empty(harness.Store.ReadKeys);
    }

    [Fact]
    public async Task HandleAsync_CustomWindow_IsHonoured()
    {
        var options = new MeshRefreshGuardOptions { MinimumInterval = TimeSpan.FromMinutes(5) };
        var harness = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(ManifestGeneratedAt(Now.AddSeconds(-60))), options: options);

        await harness.RunAsync();

        harness.AssertDenied("429", "{\"error\":\"throttled\"}");
        harness.Response.Verify(x => x.SetResponseHeader(harness.Context, "Retry-After", "240"), Times.Once);
    }

    // ---------------------------------------------------------------------------------------------
    // Denial responses must not become an oracle.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Both denials carry a fixed, minimal body with no reason text, no timing, and no hint about the
    /// catalog's contents - matching <c>Benzene.Mesh.Auth.Oidc</c>'s "no detail leakage" convention. The
    /// one thing a 429 does disclose (that a pass ran recently) is already readable by the same caller
    /// from <c>manifest.json</c>, which sits behind the same login gate.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DenialBodies_CarryNoDetail()
    {
        var manifest = ManifestGeneratedAt(Now.AddSeconds(-1));

        var forbidden = new Harness("POST", "/mesh/refresh", store: new FakeMeshArtifactStore(manifest));
        await forbidden.RunAsync();

        var throttled = new Harness("POST", "/mesh/refresh", WithRefreshHeader(),
            store: new FakeMeshArtifactStore(manifest));
        await throttled.RunAsync();

        foreach (var body in new[] { "{\"error\":\"forbidden\"}", "{\"error\":\"throttled\"}" })
        {
            Assert.DoesNotContain("generatedAt", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("manifest", body, StringComparison.OrdinalIgnoreCase);
            Assert.True(body.Length < 40);
        }

        forbidden.AssertDenied("403", "{\"error\":\"forbidden\"}");
        throttled.AssertDenied("429", "{\"error\":\"throttled\"}");
    }

    [Fact]
    public void Name_IsMeshRefreshGuard()
    {
        var harness = new Harness("POST", "/mesh/refresh");

        Assert.Equal("MeshRefreshGuard",
            new MeshRefreshGuardMiddleware<FakeHttpContext>(
                new MeshRefreshGuardOptions(), harness.Store,
                Mock.Of<IHttpRequestAdapter<FakeHttpContext>>(),
                Mock.Of<IBenzeneResponseAdapter<FakeHttpContext>>()).Name);
    }
}
