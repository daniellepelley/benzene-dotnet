using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Serialization;
using Benzene.ClaimCheck;
using Benzene.Clients;
using Benzene.Core;
using Moq;
using Xunit;

namespace Benzene.Test.ClaimCheck;

public class ClaimCheckOffloadMiddlewareTest
{
    private class SmallRequest
    {
        public string Value { get; set; } = "x";
    }

    private class UpperCaseSerializer : ISerializer
    {
        public string Serialize(Type type, object payload) => Serialize(payload);
        public string Serialize<T>(T payload) => new JsonSerializer().Serialize(payload).ToUpperInvariant();
        public object Deserialize(Type type, string payload) => new JsonSerializer().Deserialize(type, payload);
        public T Deserialize<T>(string payload) => new JsonSerializer().Deserialize<T>(payload);
    }

    private static Mock<IClaimCheckStore> NewStoreMock(string reference = "memory://topic/1")
    {
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.PutAsync(It.IsAny<string>(), It.IsAny<ClaimCheckPutContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reference);
        return store;
    }

    [Fact]
    public async Task UnderThreshold_PassesThrough_WithoutTouchingStoreOrHeaders()
    {
        var store = NewStoreMock();
        var middleware = new ClaimCheckOffloadMiddleware(store.Object, new ClaimCheckOptions());
        var context = new OutboundContext("orders:create", new SmallRequest());
        var calls = 0;

        await middleware.HandleAsync(context, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        store.Verify(x => x.PutAsync(It.IsAny<string>(), It.IsAny<ClaimCheckPutContext>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(context.Headers.ContainsKey(ClaimCheckHeaders.ClaimCheck));
        Assert.IsType<SmallRequest>(context.Request);
    }

    [Fact]
    public async Task AtOrOverThreshold_Offloads_StoresSerializedBody_SetsHeader_ReplacesRequest()
    {
        var store = NewStoreMock("memory://orders:create/abc");
        var request = new SmallRequest();
        var expectedBody = new JsonSerializer().Serialize(request);
        var options = new ClaimCheckOptions { ThresholdBytes = 1 };
        var middleware = new ClaimCheckOffloadMiddleware(store.Object, options);
        var context = new OutboundContext("orders:create", request);
        var calls = 0;

        await middleware.HandleAsync(context, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        store.Verify(x => x.PutAsync(expectedBody, It.Is<ClaimCheckPutContext>(c => c.Topic == "orders:create"), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("memory://orders:create/abc", context.Headers[ClaimCheckHeaders.ClaimCheck]);
        var placeholder = Assert.IsType<ClaimCheckPlaceholder>(context.Request);
        Assert.Equal("memory://orders:create/abc", placeholder._benzeneClaimCheck);
    }

    [Fact]
    public async Task AlwaysOffload_OffloadsATinyPayload()
    {
        var store = NewStoreMock();
        var options = new ClaimCheckOptions { AlwaysOffload = true };
        var middleware = new ClaimCheckOffloadMiddleware(store.Object, options);
        // A ten-byte-ish payload, nowhere near the (default, much larger) threshold.
        var context = new OutboundContext("orders:create", "x");

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        store.Verify(x => x.PutAsync(It.IsAny<string>(), It.IsAny<ClaimCheckPutContext>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(context.Headers.ContainsKey(ClaimCheckHeaders.ClaimCheck));
    }

    [Fact]
    public async Task StoreFailure_Propagates_AndNextNeverRuns()
    {
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.PutAsync(It.IsAny<string>(), It.IsAny<ClaimCheckPutContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        var options = new ClaimCheckOptions { ThresholdBytes = 1 };
        var middleware = new ClaimCheckOffloadMiddleware(store.Object, options);
        var context = new OutboundContext("orders:create", new SmallRequest());
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.HandleAsync(context, () => { calls++; return Task.CompletedTask; }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CustomSerializer_IsUsedToMeasureAndStoreTheBody()
    {
        var store = NewStoreMock();
        var request = new SmallRequest();
        var expectedBody = new UpperCaseSerializer().Serialize(request);
        var options = new ClaimCheckOptions { ThresholdBytes = 1, Serializer = new UpperCaseSerializer() };
        var middleware = new ClaimCheckOffloadMiddleware(store.Object, options);
        var context = new OutboundContext("orders:create", request);

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        store.Verify(x => x.PutAsync(expectedBody, It.IsAny<ClaimCheckPutContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HeaderNameOverride_IsRespected()
    {
        var store = NewStoreMock("memory://topic/1");
        var options = new ClaimCheckOptions { ThresholdBytes = 1, HeaderName = "x-claim-check" };
        var middleware = new ClaimCheckOffloadMiddleware(store.Object, options);
        var context = new OutboundContext("orders:create", new SmallRequest());

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.True(context.Headers.ContainsKey("x-claim-check"));
        Assert.False(context.Headers.ContainsKey(ClaimCheckHeaders.ClaimCheck));
    }

    // WP-7 #1: IClaimCheckStore.PutAsync already accepts a CancellationToken - the bug was only that
    // the middleware never passed the ambient one through.
    [Fact]
    public async Task AmbientTokenIsPassedIntoTheStoreCall()
    {
        using var cts = new CancellationTokenSource();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var store = new Mock<IClaimCheckStore>();
        store.Setup(x => x.PutAsync(It.IsAny<string>(), It.IsAny<ClaimCheckPutContext>(), cts.Token))
            .ReturnsAsync("memory://topic/1");
        var options = new ClaimCheckOptions { ThresholdBytes = 1 };
        var middleware = new ClaimCheckOffloadMiddleware(store.Object, options, accessor);
        var context = new OutboundContext("orders:create", new SmallRequest());

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        store.Verify(x => x.PutAsync(It.IsAny<string>(), It.IsAny<ClaimCheckPutContext>(), cts.Token), Times.Once);
    }
}
