using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Contracts;
using Xunit;

namespace Benzene.Mesh.Test;

public class MeshReportMessageHandlerTest
{
    [Fact]
    public async Task HandleAsync_ValidName_DelegatesToRegisteredPublisher_AndAccepts()
    {
        var publisher = new RecordingMeshReportPublisher();
        var handler = new MeshReportMessageHandler(publisher);
        var report = new MeshServiceReport("payments-fn", DateTimeOffset.UtcNow, "{}", null, null);

        var result = await handler.HandleAsync(report);

        Assert.Same(report, publisher.LastPublished);
        Assert.True(result.IsSuccessful);
    }

    // #242: report.Name is untrusted wire input that a publisher (ArtifactStoreMeshReportPublisher)
    // keys straight into an artifact path - these must all be rejected at the handler boundary,
    // before any publisher ever sees them, matching MeshAnnotationsMessageHandler's posture.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../manifest")]
    [InlineData("services/../manifest")]
    [InlineData("services/orders-api")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task HandleAsync_InvalidName_RejectedWithoutReachingThePublisher(string? name)
    {
        var publisher = new RecordingMeshReportPublisher();
        var handler = new MeshReportMessageHandler(publisher);
        var report = new MeshServiceReport(name!, DateTimeOffset.UtcNow, "{}", null, null);

        var result = await handler.HandleAsync(report);

        Assert.False(result.IsSuccessful);
        Assert.Null(publisher.LastPublished);
    }

    [Fact]
    public async Task HandleAsync_ReportNameEscapesServicesSubtree_LeavesManifestUntouched_EndToEnd()
    {
        // #242 end-to-end: a real MeshReportMessageHandler wired to the real
        // ArtifactStoreMeshReportPublisher/FileSystemMeshArtifactStore (the shipped default
        // deployment), fed the exact attack payload from the finding
        // (POST /mesh/report {"name":"../manifest",...}). The fleet-wide manifest.json must come
        // out exactly as it went in.
        var rootDirectory = Path.Combine(Path.GetTempPath(), "benzene-mesh-report-traversal-test-" + Guid.NewGuid());
        try
        {
            var store = new FileSystemMeshArtifactStore(rootDirectory);
            await store.PublishAsync("manifest.json", "{\"original\":true}");
            var publisher = new ArtifactStoreMeshReportPublisher(store);
            var handler = new MeshReportMessageHandler(publisher);
            var report = new MeshServiceReport("../manifest", DateTimeOffset.UtcNow, "{}", null, null);

            var result = await handler.HandleAsync(report);

            Assert.False(result.IsSuccessful);
            Assert.Equal("{\"original\":true}", await store.TryReadAsync("manifest.json"));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    private class RecordingMeshReportPublisher : IMeshReportPublisher
    {
        public MeshServiceReport? LastPublished { get; private set; }

        public Task PublishAsync(MeshServiceReport report)
        {
            LastPublished = report;
            return Task.CompletedTask;
        }
    }
}
