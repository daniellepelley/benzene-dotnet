using Amazon.CloudWatch;
using Amazon.S3;
using Amazon.XRay;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// The slice's acceptance test (work/enterprise/slice-1-config-schema.md): reproduce
/// <c>examples/AwsMesh/Mesh/Startup.cs</c>'s mesh capabilities - an S3 artifact store, an
/// <c>AwsLambdaInvoke</c>-sourced service, CloudWatch usage, an X-Ray-backed fleet plane, the mesh UI
/// and spec UI - from a <c>mesh.json</c> alone, and prove each one is reachable purely through
/// <see cref="MeshHostConfig"/>/<see cref="Startup"/>, not hand-written C#.
/// </summary>
/// <remarks>
/// Every assertion checks a registration exists (an <see cref="IServiceCollection"/> descriptor, or
/// that a wiring call completes without throwing) - never a resolved/invoked cloud client. Per the brief: "Do not call AWS. Assert on the
/// registrations, not on behaviour that needs credentials." Verified by hand that this matters: in this
/// sandbox (AWS credentials present, no AWS_REGION), <c>new AmazonS3Client()</c> /
/// <c>new AmazonCloudWatchClient()</c> / <c>new AmazonXRayClient()</c> each throw
/// <c>AmazonClientException</c> ("No RegionEndpoint or ServiceURL configured") the instant they're
/// constructed - so resolving (not just registering) any of these would make this test's pass/fail
/// depend on an ambient AWS region nobody set, exactly the flakiness the brief warns against.
/// </remarks>
public class AwsMeshParityTest
{
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Benzene.Mesh.Host.Test";
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static (Startup startup, IServiceCollection services) ConfigureAwsMeshShapedHost()
    {
        var json = """
            {
              "artifactRootDirectory": "mesh-artifacts",
              "services": [
                {
                  "name": "orders-api",
                  "source": "AwsLambdaInvoke",
                  "sourceOptions": { "functionName": "orders-api", "region": "us-east-1" }
                }
              ],
              "artifactStore": {
                "type": "s3",
                "options": { "bucket": "mesh-artifacts-bucket", "prefix": "mesh/" }
              },
              "usage": [
                { "source": "cloudwatch", "options": { "windowHours": "24" } }
              ],
              "fleet": {
                "source": "xray",
                "options": { "correlationLookbackHours": "24" }
              }
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"aws-mesh-parity-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
            var startup = new Startup(configuration);
            var services = new ServiceCollection();
            startup.ConfigureServices(services);
            return (startup, services);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool IsRegistered<T>(IServiceCollection services)
        => services.Any(sd => sd.ServiceType == typeof(T));

    // Mirrors AddMeshAggregatorWithS3(new MeshServiceRegistry(...), bucket, prefix).
    [Fact]
    public void S3ArtifactStore_ReachableFromConfig()
    {
        var (_, services) = ConfigureAwsMeshShapedHost();

        Assert.True(IsRegistered<IAmazonS3>(services));
        Assert.True(IsRegistered<IMeshArtifactStore>(services));
    }

    // Mirrors AddMeshLambdaSource() (unconditional in the host, alongside the default Http source).
    [Fact]
    public void AwsLambdaInvokeServiceSource_ReachableFromConfig()
    {
        var (_, services) = ConfigureAwsMeshShapedHost();

        // Both the default HttpMeshServiceSource (from RegisterArtifactStore -> AddMeshAggregator) and
        // LambdaMeshServiceSource (from the host's unconditional AddMeshLambdaSource()) are registered
        // as IMeshServiceSource - MeshAggregator resolves all of them via IEnumerable<>.
        Assert.Equal(2, services.Count(sd => sd.ServiceType == typeof(IMeshServiceSource)));
    }

    // Mirrors AddCloudWatchUsage(new CloudWatchUsageOptions(timeWindow: ...)).
    [Fact]
    public void CloudWatchUsage_ReachableFromConfig()
    {
        var (_, services) = ConfigureAwsMeshShapedHost();

        Assert.True(IsRegistered<IAmazonCloudWatch>(services));
        Assert.True(IsRegistered<IMeshUsageSource>(services));
    }

    // Mirrors AddXRayFleetReadModel().
    [Fact]
    public void XRayFleet_ReachableFromConfig()
    {
        var (_, services) = ConfigureAwsMeshShapedHost();

        Assert.True(IsRegistered<IAmazonXRay>(services));
        Assert.True(IsRegistered<IMeshTraceSource>(services));
        Assert.True(IsRegistered<IMeshFleetReadModel>(services));
    }

    // Mirrors .UseMeshUi("/mesh-ui", "manifest.json", "/benzene/invoke"), .UseMeshSpecUi(...),
    // .UseMeshArtifacts(...) and .UseBenzeneMessage(Path = "/benzene/invoke", fleet =>
    // fleet.UseMessageHandlers(MeshCollectorHandlers.Queries)) - all four are wired inside
    // Startup.Configure() once RegisterFleet (called during ConfigureServices) reports the fleet plane
    // is live. This does not send a request or build a runnable RequestDelegate (that needs the
    // generic host's own routing/diagnostics infrastructure, unrelated to mesh config) - it proves the
    // wiring call chain itself resolves cleanly off the S3/non-file artifact-store + X-Ray-fleet
    // registrations above, with no missing dependency and no attempt to construct a cloud client.
    [Fact]
    public void MeshUiAndSpecUiAndFleetEnvelope_WireWithoutThrowing()
    {
        var (startup, services) = ConfigureAwsMeshShapedHost();
        var provider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(provider);

        var exception = Record.Exception(() => startup.Configure(appBuilder, new FakeWebHostEnvironment()));

        Assert.Null(exception);
    }

    // The one AwsMesh capability config schema v1 deliberately cannot reach: AddMeshAwsLambdaDiscovery().
    // work/enterprise/README.md's house rules require the vanilla host to be physically incapable of
    // enumerating a cloud account - slice 3 makes that a tested invariant on its own. Proven here at the
    // assembly level: Benzene.Mesh.Host does not reference any Benzene.Mesh.Discovery.* assembly, so
    // there is no code path - config or otherwise - that could reach AwsLambdaDiscoveryProvider from
    // this host.
    [Fact]
    public void Discovery_IsNotReachableFromConfig_HostReferencesNoDiscoveryAssembly()
    {
        var referenced = typeof(Startup).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name != null)
            .ToArray();

        Assert.DoesNotContain(referenced, name => name!.StartsWith("Benzene.Mesh.Discovery", StringComparison.Ordinal));
    }
}
