using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.ClaimCheck;
using Benzene.Core;
using Moq;
using Xunit;

namespace Benzene.Test.ClaimCheck;

public class ClaimCheckHydrateMiddlewareTest
{
    private class TestContext
    {
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
    }

    private class TestHeadersGetter : IMessageHeadersGetter<TestContext>
    {
        public IDictionary<string, string> GetHeaders(TestContext context) => context.Headers;
    }

    private class TestBodySetter : IMessageBodySetter<TestContext>
    {
        public string? BodySetWith { get; private set; }
        public TestContext? ContextSetOn { get; private set; }

        public Task SetBody(TestContext context, string body)
        {
            ContextSetOn = context;
            BodySetWith = body;
            return Task.CompletedTask;
        }
    }

    private static ClaimCheckHydrateMiddleware<TestContext> Middleware(
        IClaimCheckStore store, IMessageBodySetter<TestContext>? bodySetter, ClaimCheckOptions? options = null,
        ICancellationTokenAccessor? cancellation = null)
        => new(store, new TestHeadersGetter(), bodySetter, options ?? new ClaimCheckOptions(), cancellation);

    [Fact]
    public async Task NoHeader_PassesThrough_WithoutTouchingStore()
    {
        var store = new Mock<IClaimCheckStore>();
        var bodySetter = new TestBodySetter();
        var context = new TestContext();
        var calls = 0;

        await Middleware(store.Object, bodySetter).HandleAsync(context, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        store.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(bodySetter.BodySetWith);
    }

    [Fact]
    public async Task HeaderPresent_SetterCalledWithStoredBody()
    {
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.GetAsync("memory://topic/1", It.IsAny<CancellationToken>())).ReturnsAsync("the real body");
        var bodySetter = new TestBodySetter();
        var context = new TestContext();
        context.Headers[ClaimCheckHeaders.ClaimCheck] = "memory://topic/1";
        var calls = 0;

        await Middleware(store.Object, bodySetter).HandleAsync(context, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        Assert.Equal("the real body", bodySetter.BodySetWith);
        Assert.Same(context, bodySetter.ContextSetOn);
    }

    [Fact]
    public async Task MissingBlob_ThrowsNamingTheReference()
    {
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.GetAsync("memory://topic/missing", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var bodySetter = new TestBodySetter();
        var context = new TestContext();
        context.Headers[ClaimCheckHeaders.ClaimCheck] = "memory://topic/missing";

        var ex = await Assert.ThrowsAsync<ClaimCheckNotFoundException>(() =>
            Middleware(store.Object, bodySetter).HandleAsync(context, () => Task.CompletedTask));

        Assert.Equal("memory://topic/missing", ex.Reference);
        Assert.Contains("memory://topic/missing", ex.Message);
    }

    [Fact]
    public async Task NoBodySetterRegistered_ThrowsNamingTheContextType()
    {
        var store = new Mock<IClaimCheckStore>();
        var context = new TestContext();
        context.Headers[ClaimCheckHeaders.ClaimCheck] = "memory://topic/1";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Middleware(store.Object, bodySetter: null).HandleAsync(context, () => Task.CompletedTask));

        Assert.Contains(nameof(TestContext), ex.Message);
        store.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HeaderNameOverride_IsRespectedOnRead()
    {
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.GetAsync("memory://topic/1", It.IsAny<CancellationToken>())).ReturnsAsync("body");
        var bodySetter = new TestBodySetter();
        var context = new TestContext();
        context.Headers["x-claim-check"] = "memory://topic/1";
        var options = new ClaimCheckOptions { HeaderName = "x-claim-check" };

        await Middleware(store.Object, bodySetter, options).HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal("body", bodySetter.BodySetWith);
    }

    // WP-7 #1: IClaimCheckStore.GetAsync already accepts a CancellationToken - the bug was only that
    // the middleware never passed the ambient one through. Proves the ambient token (resolved via
    // ICancellationTokenAccessor, the same mechanism the batch applications seed) actually reaches the
    // store call - not just that CancellationToken.None was silently passed regardless.
    [Fact]
    public async Task AmbientTokenIsPassedIntoTheStoreCall()
    {
        using var cts = new CancellationTokenSource();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.GetAsync("memory://topic/1", cts.Token)).ReturnsAsync("body");
        var bodySetter = new TestBodySetter();
        var context = new TestContext();
        context.Headers[ClaimCheckHeaders.ClaimCheck] = "memory://topic/1";

        await Middleware(store.Object, bodySetter, cancellation: accessor)
            .HandleAsync(context, () => Task.CompletedTask);

        store.Verify(x => x.GetAsync("memory://topic/1", cts.Token), Times.Once);
    }

    // A hung store call is bounded once the ambient token is cancelled - proof the token genuinely
    // reaches the store's own in-flight call (the ruling's "hung fake store + UseTimeout" scenario,
    // reproduced directly against the store call rather than a full pipeline).
    [Fact]
    public async Task AmbientTokenCancellation_BoundsAHungStoreCall()
    {
        using var cts = new CancellationTokenSource();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.GetAsync("memory://topic/1", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                // Never completes on its own - only cancellation can end this await.
                await Task.Delay(Timeout.Infinite, token);
                return (string?)"unreachable";
            });
        var bodySetter = new TestBodySetter();
        var context = new TestContext();
        context.Headers[ClaimCheckHeaders.ClaimCheck] = "memory://topic/1";

        var handleTask = Middleware(store.Object, bodySetter, cancellation: accessor)
            .HandleAsync(context, () => Task.CompletedTask);

        cts.Cancel();

        // A generous outer bound: this only guards against a genuine "never returns" regression, not
        // ordinary scheduling latency under host contention - the cancellation itself is immediate.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handleTask)
            .WaitAsync(TimeSpan.FromSeconds(30));
    }
}
