using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Aggregator;
using Xunit;

namespace Benzene.Mesh.Test;

public class FileSystemMeshArtifactStoreTest : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "benzene-mesh-test-" + Guid.NewGuid());

    [Fact]
    public async Task TryReadAsync_NothingPublished_ReturnsNull()
    {
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        Assert.Null(await store.TryReadAsync("manifest.json"));
    }

    [Fact]
    public async Task PublishAsync_ThenTryReadAsync_RoundTrips()
    {
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        await store.PublishAsync("manifest.json", "{\"hello\":\"world\"}");

        Assert.Equal("{\"hello\":\"world\"}", await store.TryReadAsync("manifest.json"));
    }

    [Fact]
    public async Task PublishAsync_NestedRelativePath_CreatesDirectory()
    {
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        await store.PublishAsync("services/orders-api.json", "{}");

        Assert.Equal("{}", await store.TryReadAsync("services/orders-api.json"));
    }

    [Fact]
    public async Task PublishAsync_Overwrite_ReplacesContent()
    {
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        await store.PublishAsync("manifest.json", "{\"version\":1}");
        await store.PublishAsync("manifest.json", "{\"version\":2}");

        Assert.Equal("{\"version\":2}", await store.TryReadAsync("manifest.json"));
    }

    [Fact]
    public async Task PublishAsync_LeavesNoTemporaryFileBehind()
    {
        // #151: PublishAsync writes to a temp file and renames it into place - verify that temp file
        // never lingers next to the real artifact after a successful publish.
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        await store.PublishAsync("manifest.json", "{}");

        var leftovers = Directory.GetFiles(_rootDirectory).Where(f => f.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task PublishAsync_ConcurrentWithTryReadAsync_NeverObservesATornRead()
    {
        // #151: PublishAsync used to truncate-then-write in place (File.WriteAllTextAsync), so a
        // concurrent TryReadAsync against the very same process (this store is the shipped Mesh Host's
        // default, polled and read back by one process on a timer) could observe a torn read - some
        // prefix of the old content plus some prefix of the new, or a truncated/empty file. Large
        // content makes the write take long enough for a concurrent reader to have a real chance of
        // catching it mid-write; an atomic temp-file-then-rename write makes that structurally
        // impossible regardless of timing - every read is either the complete old value or the
        // complete new one.
        var store = new FileSystemMeshArtifactStore(_rootDirectory);
        var oldContent = new string('a', 4_000_000);
        var newContent = new string('b', 4_000_000);
        await store.PublishAsync("manifest.json", oldContent);

        using var cts = new CancellationTokenSource();
        var observedTornRead = false;
        var reader = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await store.TryReadAsync("manifest.json");
                if (read != null && read != oldContent && read != newContent)
                {
                    observedTornRead = true;
                    break;
                }
            }
        });

        await store.PublishAsync("manifest.json", newContent);
        cts.Cancel();
        await reader;

        Assert.False(observedTornRead, "TryReadAsync observed content that was neither the old nor the new value.");
        Assert.Equal(newContent, await store.TryReadAsync("manifest.json"));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("services/../../escape.json")]
    [InlineData("../../etc/passwd")]
    public async Task PublishAsync_PathEscapingRoot_IsRejected(string relativePath)
    {
        // The relative path can carry a service name from an untrusted push report, so a traversal
        // sequence must not let a write escape the store root.
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => store.PublishAsync(relativePath, "{}"));
    }

    [Theory]
    [InlineData("services/../manifest.json")]
    [InlineData("services/../topics.json")]
    [InlineData("services/./../manifest.json")]
    public async Task PublishAsync_PathEscapesIntendedSubtreeButStaysInsideRoot_IsRejected(string relativePath)
    {
        // #242: "services/../manifest.json" normalizes to "{root}/manifest.json" - still *inside*
        // the store root, so the older root-containment-only check in ResolveWithinRoot let this
        // through. A caller building "services/{name}.json" from an untrusted push-report name
        // (ArtifactStoreMeshReportPublisher) could use it to overwrite manifest.json (or any other
        // top-level artifact) despite only ever being meant to touch the services/ subtree. This is
        // the exact gap the class's own doc comment claimed to close but didn't - pinned here at the
        // store level, independent of whichever caller-side validation exists further up the stack.
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => store.PublishAsync(relativePath, "{\"attacker\":\"controlled\"}"));

        Assert.Null(await store.TryReadAsync("manifest.json"));
    }

    [Fact]
    public async Task TryReadAsync_PathEscapingRoot_IsRejected()
    {
        var store = new FileSystemMeshArtifactStore(_rootDirectory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => store.TryReadAsync("../../etc/passwd"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
