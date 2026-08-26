using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Http;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

public class MeshArtifactMiddlewareTest
{
    public class FakeHttpContext : IHttpContext
    {
    }

    // Hand-written fake IMeshArtifactStore (rather than a mock) so the tests can also assert on
    // exactly which keys the middleware asked the store to resolve - the load-bearing evidence for
    // the "unknown path never touches the store" and "traversal attempt never touches the store"
    // cases.
    private sealed class FakeMeshArtifactStore : IMeshArtifactStore
    {
        private readonly Dictionary<string, string> _content;

        public FakeMeshArtifactStore(Dictionary<string, string> content) => _content = content;

        public List<string> ReadKeys { get; } = new();

        public Task PublishAsync(string relativePath, string content) =>
            throw new NotSupportedException("The middleware under test never publishes.");

        public Task<string?> TryReadAsync(string relativePath)
        {
            ReadKeys.Add(relativePath);
            return Task.FromResult(_content.TryGetValue(relativePath, out var value) ? value : null);
        }
    }

    // Simulates one of the three cloud artifact stores' documented, tested contract: re-throw on a
    // non-404 failure (a transient S3/Blob/GCS hiccup), rather than degrading to null like a genuine
    // "not found".
    private sealed class ThrowingMeshArtifactStore : IMeshArtifactStore
    {
        public Task PublishAsync(string relativePath, string content) =>
            throw new NotSupportedException("The middleware under test never publishes.");

        public Task<string?> TryReadAsync(string relativePath) =>
            throw new InvalidOperationException("simulated transient store failure");
    }

    private static (Mock<IHttpRequestAdapter<FakeHttpContext>> RequestAdapter, Mock<IBenzeneResponseAdapter<FakeHttpContext>> ResponseAdapter)
        CreateAdapters(FakeHttpContext context, string method, string path)
    {
        var requestAdapter = new Mock<IHttpRequestAdapter<FakeHttpContext>>();
        requestAdapter.Setup(x => x.Map(context)).Returns(new HttpRequest { Method = method, Path = path });

        var responseAdapter = new Mock<IBenzeneResponseAdapter<FakeHttpContext>>();

        return (requestAdapter, responseAdapter);
    }

    [Fact]
    public async Task HandleAsync_KnownPath_ReturnsStoredContent()
    {
        var context = new FakeHttpContext();
        var (requestAdapter, responseAdapter) = CreateAdapters(context, "GET", "/manifest.json");
        var store = new FakeMeshArtifactStore(new Dictionary<string, string> { ["manifest.json"] = "{\"services\":[]}" });
        var middleware = new MeshArtifactMiddleware<FakeHttpContext>(store, requestAdapter.Object, responseAdapter.Object);

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(new[] { "manifest.json" }, store.ReadKeys);
        responseAdapter.Verify(x => x.SetStatusCode(context, "200"), Times.Once);
        responseAdapter.Verify(x => x.SetContentType(context, "application/json"), Times.Once);
        responseAdapter.Verify(x => x.SetBody(context, "{\"services\":[]}"), Times.Once);
        responseAdapter.Verify(x => x.FinalizeAsync(context), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_UnknownArtifactPath_FallsThroughToNext()
    {
        var context = new FakeHttpContext();
        var (requestAdapter, responseAdapter) = CreateAdapters(context, "GET", "/other.json");
        var store = new FakeMeshArtifactStore(new Dictionary<string, string>());
        var middleware = new MeshArtifactMiddleware<FakeHttpContext>(store, requestAdapter.Object, responseAdapter.Object);

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Empty(store.ReadKeys);
        responseAdapter.Verify(x => x.SetStatusCode(context, It.IsAny<string>()), Times.Never);
        responseAdapter.Verify(x => x.SetBody(context, It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("/../../../etc/passwd")]
    [InlineData("/services/../../../etc/passwd")]
    public async Task HandleAsync_DirectoryTraversalAttempt_IsRefused(string requestPath)
    {
        var context = new FakeHttpContext();
        var (requestAdapter, responseAdapter) = CreateAdapters(context, "GET", requestPath);
        var store = new FakeMeshArtifactStore(new Dictionary<string, string>());
        var middleware = new MeshArtifactMiddleware<FakeHttpContext>(store, requestAdapter.Object, responseAdapter.Object);

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        // Neither shape matches the artifact allow-list (no literal name, and not ending in
        // "services/*.json"), so the middleware falls through without ever asking the store to
        // resolve a path containing "..". Any store-level containment check (e.g.
        // FileSystemMeshArtifactStore.ResolveWithinRoot) is a second, independent line of defense -
        // this test proves the middleware itself never forwards a traversal-shaped key at all.
        Assert.True(nextCalled);
        Assert.Empty(store.ReadKeys);
    }

    // Regression for #72 (worth-fixing, live-verified): HandleAsync called _store.TryReadAsync with no
    // try/catch, but all three cloud artifact stores deliberately re-throw on a non-404 failure - so a
    // transient hiccup crashed this middleware, the mesh UI's primary read path, as a raw unhandled 500.
    [Fact]
    public async Task HandleAsync_StoreThrows_DegradesToAClean503_RatherThanAnUnhandledException()
    {
        var context = new FakeHttpContext();
        var (requestAdapter, responseAdapter) = CreateAdapters(context, "GET", "/manifest.json");
        var store = new ThrowingMeshArtifactStore();
        var middleware = new MeshArtifactMiddleware<FakeHttpContext>(store, requestAdapter.Object, responseAdapter.Object);

        var nextCalled = false;
        var exception = await Record.ExceptionAsync(() =>
            middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; }));

        Assert.Null(exception);
        Assert.False(nextCalled);
        responseAdapter.Verify(x => x.SetStatusCode(context, "503"), Times.Once);
        responseAdapter.Verify(x => x.SetContentType(context, "application/json"), Times.Once);
        // No detail leakage: the exception's message/type never reaches the response body.
        responseAdapter.Verify(x => x.SetBody(context, "{\"error\":\"unavailable\"}"), Times.Once);
        responseAdapter.Verify(x => x.FinalizeAsync(context), Times.Once);
    }

    [Fact]
    public void Name_IsMeshArtifacts()
    {
        var context = new FakeHttpContext();
        var (requestAdapter, responseAdapter) = CreateAdapters(context, "GET", "/manifest.json");
        var store = new FakeMeshArtifactStore(new Dictionary<string, string>());
        var middleware = new MeshArtifactMiddleware<FakeHttpContext>(store, requestAdapter.Object, responseAdapter.Object);

        Assert.Equal("MeshArtifacts", middleware.Name);
    }
}
