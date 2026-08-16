using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http;
using Benzene.Http.BenzeneMessage;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Testing;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Mesh.Test;

/// <summary>
/// End-to-end guard for the AwsMesh refresh endpoint's protection model, exercised through the real
/// (payload format 1.0) API Gateway host rather than against the middleware in isolation. Examples
/// aren't in the CI gate, so - exactly as <see cref="AwsMeshFleetEndpointTest"/> does for the fleet
/// endpoint - this library-side test stands in for <c>examples/AwsMesh/Mesh/Startup.cs</c>'s wiring.
/// <para>
/// Login itself is out of scope here (<c>Benzene.Mesh.Auth.Oidc</c> has its own tests, and it sits in
/// front of everything below): what is under test is the layer <em>after</em> authentication - the CSRF
/// header, the throttle, the route table's method scoping, and the envelope endpoint's topic filter.
/// </para>
/// </summary>
public class AwsMeshRefreshEndpointTest
{
    /// <summary>
    /// Stands in for the example's <c>MeshAggregateHandler</c>: same topic and same single POST
    /// endpoint, but it records an invocation instead of costing money. "Did this run?" is the
    /// assertion that actually matters in every test below.
    /// </summary>
    [Message(MeshAggregatorTopics.Aggregate)]
    [HttpEndpoint("POST", "/mesh/refresh")]
    public class SpyAggregateHandler : IMessageHandler<Void, string>
    {
        public static int Invocations;

        public Task<IBenzeneResult<string>> HandleAsync(Void request)
        {
            Invocations++;
            return Task.FromResult(BenzeneResult.Created("ran"));
        }
    }

    private sealed class StubArtifactStore : IMeshArtifactStore
    {
        private readonly string? _manifest;

        public StubArtifactStore(string? manifest) => _manifest = manifest;

        public Task PublishAsync(string relativePath, string content) => Task.CompletedTask;

        public Task<string?> TryReadAsync(string relativePath) => Task.FromResult(_manifest);
    }

    private static string ManifestGeneratedAt(DateTimeOffset at)
        => "{\"generatedAtUtc\":\"" + at.ToString("O") + "\",\"services\":[]}";

    /// <summary>
    /// The mesh Lambda's HTTP sub-pipeline, minus the login gate: the refresh guard first, then the
    /// envelope endpoint (with the topic filter that keeps it to read queries), then the handler router.
    /// </summary>
    private static AwsLambdaBenzeneTestHost CreateHost(string? manifest = null, TimeSpan? window = null)
    {
        SpyAggregateHandler.Invocations = 0;

        var guardOptions = new MeshRefreshGuardOptions();
        if (window.HasValue)
        {
            guardOptions.MinimumInterval = window.Value;
        }

        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.UsingBenzene(x =>
            {
                x.AddBenzene();
                x.AddMessageHandlers(new[] { typeof(SpyAggregateHandler) });
                x.AddHttpMessageHandlers();
                x.AddSingleton<IMeshArtifactStore>(new StubArtifactStore(manifest));
            }))
            .Configure(app => app
                .UseApiGateway(http => http
                    .UseMeshRefreshGuard(guardOptions)
                    .UseBenzeneMessage(new BenzeneMessageHttpOptions
                        {
                            Path = "/benzene/invoke",
                            TopicFilter = topic => topic.StartsWith("benzene:mesh:query:", StringComparison.OrdinalIgnoreCase),
                        },
                        fleet => fleet.UseMessageHandlers(typeof(SpyAggregateHandler)))
                    .UseMessageHandlers(typeof(SpyAggregateHandler))))
            .BuildHost();
    }

    [Fact]
    public async Task PostRefresh_WithTheCustomHeader_RunsThePass()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(HttpBuilder
            .Create("POST", "/mesh/refresh")
            .WithHeader("X-Benzene-Refresh", "1"));

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(1, SpyAggregateHandler.Invocations);
    }

    /// <summary>
    /// The CSRF case: a cross-site <c>&lt;form method="post"&gt;</c> cannot set a custom header, so this is
    /// exactly the request such a form produces once <c>SameSite=Lax</c> is (for whatever reason - an old
    /// browser, a known Lax edge case) not doing its job.
    /// </summary>
    [Fact]
    public async Task PostRefresh_WithoutTheCustomHeader_Is403AndRunsNothing()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(HttpBuilder.Create("POST", "/mesh/refresh"));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(0, SpyAggregateHandler.Invocations);
        Assert.DoesNotContain("manifest", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of the CSRF story: a cross-origin <c>fetch()</c> that <em>does</em> set the header
    /// must first pass a CORS preflight. Nothing on this pipeline answers <c>OPTIONS /mesh/refresh</c>
    /// with <c>Access-Control-Allow-Headers</c>, so the preflight is never approved and the real request
    /// is never sent. Pinned here so adding a permissive CORS middleware later shows up as a failure.
    /// </summary>
    [Fact]
    public async Task OptionsRefresh_IsNotApprovedAsAPreflight()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(HttpBuilder
            .Create("OPTIONS", "/mesh/refresh")
            .WithHeader("Origin", "https://evil.example")
            .WithHeader("Access-Control-Request-Method", "POST")
            .WithHeader("Access-Control-Request-Headers", "x-benzene-refresh"));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(0, SpyAggregateHandler.Invocations);
        Assert.False(response.Headers != null &&
                     response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// <c>SameSite=Lax</c> sends the session cookie on a top-level GET navigation, so a GET that could
    /// trigger a pass would be CSRF-able from a bare link. It is refused twice over: the guard answers
    /// first, and even without the guard the route table maps no GET (see <see cref="MeshRefreshRoutingTest"/>).
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task NonPostMethodsOnRefresh_NeverRunThePass(string method)
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(HttpBuilder
            .Create(method, "/mesh/refresh")
            .WithHeader("X-Benzene-Refresh", "1"));

        // With the header present the guard lets it through, and the router then finds no route for the
        // method - a 404. Either way the pass does not run, which is the property under test.
        Assert.Equal(404, response.StatusCode);
        Assert.Equal(0, SpyAggregateHandler.Invocations);
    }

    /// <summary>
    /// A crafted path that still routes to the handler must still be guarded - the guard's normalization
    /// and the route table's must not disagree. <see cref="MeshRefreshRoutingTest"/> proves each of these
    /// really does route; this proves the guard sees them through the whole host.
    /// </summary>
    [Theory]
    [InlineData("/mesh/refresh/")]
    [InlineData("//mesh//refresh")]
    [InlineData("/MESH/REFRESH")]
    [InlineData("/mesh/refresh?force=1")]
    public async Task PostRefresh_ViaACraftedPathSpelling_IsStillGuarded(string path)
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(HttpBuilder.Create("POST", path));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(0, SpyAggregateHandler.Invocations);
    }

    [Fact]
    public async Task PostRefresh_InsideTheThrottleWindow_Is429AndRunsNothing()
    {
        var host = CreateHost(ManifestGeneratedAt(DateTimeOffset.UtcNow.AddSeconds(-2)));

        var response = await host.SendApiGatewayAsync(HttpBuilder
            .Create("POST", "/mesh/refresh")
            .WithHeader("X-Benzene-Refresh", "1"));

        Assert.Equal(429, response.StatusCode);
        Assert.Equal(0, SpyAggregateHandler.Invocations);
        Assert.NotNull(response.Headers);
        Assert.True(response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task PostRefresh_OutsideTheThrottleWindow_RunsThePass()
    {
        var host = CreateHost(ManifestGeneratedAt(DateTimeOffset.UtcNow.AddHours(-1)));

        var response = await host.SendApiGatewayAsync(HttpBuilder
            .Create("POST", "/mesh/refresh")
            .WithHeader("X-Benzene-Refresh", "1"));

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(1, SpyAggregateHandler.Invocations);
    }

    /// <summary>
    /// Hammering: with a fresh manifest, repeated requests are all refused. (Sequential, deliberately -
    /// this throttle is a rate limiter, not a lock, and does not claim to serialise concurrent callers.)
    /// </summary>
    [Fact]
    public async Task PostRefresh_HammeredInsideTheWindow_RunsNothing()
    {
        var host = CreateHost(ManifestGeneratedAt(DateTimeOffset.UtcNow));

        for (var i = 0; i < 20; i++)
        {
            var response = await host.SendApiGatewayAsync(HttpBuilder
                .Create("POST", "/mesh/refresh")
                .WithHeader("X-Benzene-Refresh", "1"));

            Assert.Equal(429, response.StatusCode);
        }

        Assert.Equal(0, SpyAggregateHandler.Invocations);
    }

    /// <summary>
    /// The bypass this hardening pass turned up, pinned so it cannot come back. Handler discovery in
    /// Benzene is a single process-wide union of every <c>AddMessageHandlers</c> call, so an inner
    /// <c>UseBenzeneMessage</c> pipeline configured with a narrow handler-type list can still route ANY
    /// topic registered anywhere in the app. Without <see cref="BenzeneMessageHttpOptions.TopicFilter"/>,
    /// a POSTed envelope naming the aggregate topic would run a full pass down a path that never meets
    /// the refresh guard at all - no header required, no throttle applied.
    /// </summary>
    [Fact]
    public async Task PostAggregateTopic_ToTheEnvelopeEndpoint_IsRefusedByTheTopicFilter()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(HttpBuilder
            .Create("POST", "/benzene/invoke", new
            {
                topic = MeshAggregatorTopics.Aggregate,
                headers = new Dictionary<string, string>(),
                body = "{}",
            }));

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(0, SpyAggregateHandler.Invocations);
    }

    /// <summary>
    /// The same envelope, with the topic filter removed, DOES reach the handler - the evidence that the
    /// filter above is load-bearing rather than belt-and-braces, and that
    /// <c>UseMessageHandlers(MeshCollectorHandlers.Queries)</c> alone never scoped this endpoint.
    /// </summary>
    [Fact]
    public async Task PostAggregateTopic_ToAnUnfilteredEnvelopeEndpoint_ReachesTheHandler()
    {
        SpyAggregateHandler.Invocations = 0;

        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.UsingBenzene(x =>
            {
                x.AddBenzene();
                x.AddMessageHandlers(new[] { typeof(SpyAggregateHandler) });
                x.AddHttpMessageHandlers();
                x.AddSingleton<IMeshArtifactStore>(new StubArtifactStore(null));
            }))
            .Configure(app => app
                .UseApiGateway(http => http
                    .UseMeshRefreshGuard()
                    .UseBenzeneMessage(new BenzeneMessageHttpOptions { Path = "/benzene/invoke" },
                        fleet => fleet.UseMessageHandlers(typeof(SpyAggregateHandler)))
                    .UseMessageHandlers(typeof(SpyAggregateHandler))))
            .BuildHost();

        await host.SendApiGatewayAsync(HttpBuilder
            .Create("POST", "/benzene/invoke", new
            {
                topic = MeshAggregatorTopics.Aggregate,
                headers = new Dictionary<string, string>(),
                body = "{}",
            }));

        Assert.Equal(1, SpyAggregateHandler.Invocations);
    }

    /// <summary>The read queries the endpoint exists for are unaffected by the filter.</summary>
    [Fact]
    public async Task PostQueryTopic_ToTheEnvelopeEndpoint_IsNotRefusedByTheTopicFilter()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(HttpBuilder
            .Create("POST", "/benzene/invoke", new
            {
                topic = "benzene:mesh:query:fleet",
                headers = new Dictionary<string, string>(),
                body = "{}",
            }));

        // No query handler is registered on this cut-down host, so the router 404s it - but crucially
        // with "No handler found", not the filter's "not available on this endpoint".
        Assert.DoesNotContain("not available on this endpoint", response.Body);
    }
}
