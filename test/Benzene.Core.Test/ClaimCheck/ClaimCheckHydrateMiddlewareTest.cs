using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.ClaimCheck;
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
        IClaimCheckStore store, IMessageBodySetter<TestContext>? bodySetter, ClaimCheckOptions? options = null)
        => new(store, new TestHeadersGetter(), bodySetter, options ?? new ClaimCheckOptions());

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
}
