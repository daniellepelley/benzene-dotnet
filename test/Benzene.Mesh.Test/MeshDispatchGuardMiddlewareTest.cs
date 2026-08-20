using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Http;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Dispatch;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// The guard in front of the mesh's dispatch endpoint — the one surface that fires a caller's payload
/// into a real handler.
/// </summary>
/// <remarks>
/// Adversarial as much as functional. Several of these exist to pin that a refusal is shaped for its
/// reader: a rate-limited human has to be told they are going too fast, because a bare HTTP status
/// renders in the mesh UI as an unexplained failure, and a reader who cannot tell "throttled" from
/// "broken" files a bug against the wrong thing.
/// </remarks>
public class MeshDispatchGuardMiddlewareTest
{
    public class FakeHttpContext : IHttpContext
    {
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 30, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeHttpContext Context { get; } = new();
        public Mock<IBenzeneResponseAdapter<FakeHttpContext>> Response { get; } = new();
        public bool NextCalled { get; private set; }

        private readonly MeshDispatchGuardMiddleware<FakeHttpContext> _middleware;

        public Harness(
            string path = "/mesh/dispatch",
            IDictionary<string, string>? headers = null,
            string? email = "someone@example.com",
            MeshDispatchGuardOptions? options = null,
            MeshDispatchRateLimiter? limiter = null)
        {
            var request = new Mock<IHttpRequestAdapter<FakeHttpContext>>();
            request.Setup(x => x.Map(Context)).Returns(new HttpRequest
            {
                Method = "POST",
                Path = path,
                Headers = headers ?? Headers(),
            });

            _middleware = new MeshDispatchGuardMiddleware<FakeHttpContext>(
                options ?? new MeshDispatchGuardOptions(),
                new MeshDispatchIdentity { Email = email },
                limiter ?? new MeshDispatchRateLimiter(() => Now),
                request.Object, Response.Object, routeFinder: null, logger: null);
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
        }

        public void AssertRefused(string httpStatus)
        {
            Assert.False(NextCalled);
            Response.Verify(x => x.SetStatusCode(Context, httpStatus), Times.Once);
            Response.Verify(x => x.FinalizeAsync(Context), Times.Once);
        }

        public string CapturedBody() => Response.Invocations
            .Where(i => i.Method.Name == "SetBody")
            .Select(i => i.Arguments[1] as string ?? string.Empty)
            .LastOrDefault() ?? string.Empty;
    }

    private static Dictionary<string, string> Headers(string? dispatchHeader = "1", string? contentLength = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dispatchHeader != null) headers["X-Benzene-Dispatch"] = dispatchHeader;
        if (contentLength != null) headers["Content-Length"] = contentLength;
        return headers;
    }

    [Fact]
    public async Task AnythingThatIsNotTheDispatchEndpoint_IsUntouched()
    {
        var harness = new Harness(path: "/mesh-ui", headers: Headers(dispatchHeader: null), email: null);
        await harness.RunAsync();
        harness.AssertAllowed();
    }

    [Fact]
    public async Task ASignedInCallerWithTheHeader_IsAllowedThrough()
    {
        var harness = new Harness();
        await harness.RunAsync();
        harness.AssertAllowed();
    }

    [Fact]
    public async Task WithoutTheCsrfHeader_IsRefusedAndTellsTheCallerNothing()
    {
        // A cross-site form cannot set a custom header. The denial deliberately carries no detail —
        // this caller is an attacker or a bug, and neither is owed a diagnosis.
        var harness = new Harness(headers: Headers(dispatchHeader: null));
        await harness.RunAsync();

        harness.AssertRefused("403");
        Assert.Equal("{\"error\":\"forbidden\"}", harness.CapturedBody());
    }

    [Fact]
    public async Task WithoutAnIdentity_FailsClosed()
    {
        // Reaching the guard with no identity means the session gate is missing or mounted below it.
        // Allowing that would produce dispatches nobody can be held to, which is precisely what the
        // audit record exists to prevent — so the wiring mistake announces itself.
        var harness = new Harness(email: null);
        await harness.RunAsync();
        harness.AssertRefused("403");
    }

    [Fact]
    public async Task AnOversizedPayload_IsRefusedBeforeAnythingIsParsed()
    {
        var harness = new Harness(
            headers: Headers(contentLength: "999999"),
            options: new MeshDispatchGuardOptions { MaxRequestBytes = 1024 });
        await harness.RunAsync();

        harness.AssertRefused("413");
        // Envelope-shaped, so the console renders the reason rather than a generic failure.
        Assert.Contains("\"statusCode\":\"bad-request\"", harness.CapturedBody());
    }

    [Fact]
    public async Task PastTheRateLimit_IsRefusedAsAnEnvelopeTheConsoleCanRender()
    {
        // THE POINT OF THIS TEST. A bare HTTP 429 falls into the UI's generic failure path and reads
        // as "something broke"; the reader then reports a bug instead of slowing down.
        var limiter = new MeshDispatchRateLimiter(() => Now);
        var options = new MeshDispatchGuardOptions { MaxPerMinutePerIdentity = 2 };

        for (var i = 0; i < 2; i++)
        {
            var allowed = new Harness(options: options, limiter: limiter);
            await allowed.RunAsync();
            allowed.AssertAllowed();
        }

        var refused = new Harness(options: options, limiter: limiter);
        await refused.RunAsync();

        refused.AssertRefused("429");
        Assert.Contains("\"statusCode\":\"too-many-requests\"", refused.CapturedBody());
        refused.Response.Verify(x => x.SetResponseHeader(refused.Context, "Retry-After", "30"), Times.Once);
    }

    [Fact]
    public async Task TheRateLimitIsPerIdentity_SoOnePersonCannotSpendAnother_sAllowance()
    {
        var limiter = new MeshDispatchRateLimiter(() => Now);
        var options = new MeshDispatchGuardOptions { MaxPerMinutePerIdentity = 1 };

        var first = new Harness(email: "a@example.com", options: options, limiter: limiter);
        await first.RunAsync();
        first.AssertAllowed();

        var second = new Harness(email: "b@example.com", options: options, limiter: limiter);
        await second.RunAsync();
        second.AssertAllowed();

        var firstAgain = new Harness(email: "a@example.com", options: options, limiter: limiter);
        await firstAgain.RunAsync();
        firstAgain.AssertRefused("429");
    }

    [Theory]
    [InlineData("/MESH/DISPATCH")]
    [InlineData("//mesh//dispatch")]
    [InlineData("/mesh/dispatch?x=1")]
    public async Task AnOddSpellingOfTheGuardedPath_IsStillGuarded(string path)
    {
        // The guard canonicalises exactly as the router does, or a crafted spelling reaches the
        // handler around the guard rather than through it.
        var harness = new Harness(path: path, headers: Headers(dispatchHeader: null));
        await harness.RunAsync();
        harness.AssertRefused("403");
    }

    [Fact]
    public void TheLimiterRollsWithTheMinute_RatherThanBanningForever()
    {
        var clock = Now;
        var limiter = new MeshDispatchRateLimiter(() => clock);

        Assert.True(limiter.TryAcquire("k", 1, out _));
        Assert.False(limiter.TryAcquire("k", 1, out var retryAfter));
        Assert.Equal(30, retryAfter);

        clock = Now.AddMinutes(1);
        Assert.True(limiter.TryAcquire("k", 1, out _));
    }

    [Fact]
    public void ALimitOfZero_DisablesTheCheckRatherThanRefusingEverything()
    {
        // An operator turning a limit off must get "off", not "nothing gets through" — the opposite
        // reading would be a very quiet outage.
        var limiter = new MeshDispatchRateLimiter(() => Now);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire("k", 0, out _));
        }
    }
}
