using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.CodeGen.Cli.Core.Commands.Build;
using Benzene.CodeGen.Cli.Core.Commands.Spec;
using Benzene.Core.MessageHandlers;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// Phase 3: `benzene build`/`benzene spec` no longer require a deployed AWS Lambda. This covers the
// --file source end to end (ClientCodeBuilder generating real files on disk from a spec.json
// artifact, offline, no AWS SDK call attempted) plus the mutually-exclusive source validation that
// SpecSourceResolver enforces for every source combination.
public class BuildSourcesTest
{
    public class CreateOrder { public string Id { get; set; } = ""; }
    public class Order { public string Id { get; set; } = ""; public string Status { get; set; } = ""; }

    private static string WriteSpecFile(string title = null)
    {
        var definitions = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("order:create", typeof(CreateOrder), typeof(Order)),
        };
        var document = definitions.ToEventServiceDocument();
        if (!string.IsNullOrEmpty(title))
        {
            document.Info = new OpenApiInfo { Title = title };
        }

        var path = Path.Combine(Path.GetTempPath(), $"benzene-build-{Guid.NewGuid():N}.spec.json");
        File.WriteAllText(path, document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));
        return path;
    }

    [Fact]
    public async Task FileSpecSource_ReturnsFileContentVerbatim()
    {
        var path = WriteSpecFile();
        try
        {
            var expected = await File.ReadAllTextAsync(path);
            var source = new FileSpecSource(path);

            var actual = await source.GetSpecJsonAsync(new Benzene.Schema.OpenApi.SpecRequest("benzene", "json"));

            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSpecSource_MissingFile_ThrowsFileNotFoundException()
    {
        var source = new FileSpecSource(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.spec.json"));

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => source.GetSpecJsonAsync(new Benzene.Schema.OpenApi.SpecRequest("benzene", "json")));
    }

    [Fact]
    public async Task Build_FromFile_WithServiceName_GeneratesExpectedClientFiles_Offline()
    {
        var specPath = WriteSpecFile();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"benzene-build-out-{Guid.NewGuid():N}");
        try
        {
            var payload = new BuildPayload
            {
                File = specPath,
                Output = "client",
                ServiceName = "Orders",
                Directory = outputDirectory,
            };

            await new ClientCodeBuilder().Build(payload);

            Assert.True(File.Exists(Path.Combine(outputDirectory, "OrdersServiceClient.cs")));
            Assert.True(File.Exists(Path.Combine(outputDirectory, "IOrdersServiceClient.cs")));

            var clientSource = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "OrdersServiceClient.cs"));
            Assert.Contains("CreateOrderAsync", clientSource);
        }
        finally
        {
            File.Delete(specPath);
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Build_FromFile_NoServiceNameOverride_UsesDocumentTitle()
    {
        var specPath = WriteSpecFile(title: "TitledService");
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"benzene-build-out-{Guid.NewGuid():N}");
        try
        {
            var payload = new BuildPayload
            {
                File = specPath,
                Output = "client",
                Directory = outputDirectory,
            };

            await new ClientCodeBuilder().Build(payload);

            Assert.True(File.Exists(Path.Combine(outputDirectory, "TitledServiceServiceClient.cs")));
        }
        finally
        {
            File.Delete(specPath);
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Build_NoSourceGiven_Throws()
    {
        var payload = new BuildPayload { Output = "client", Directory = Path.GetTempPath() };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => new ClientCodeBuilder().Build(payload));
        Assert.Contains("--file", exception.Message);
        Assert.Contains("--url", exception.Message);
        Assert.Contains("--mesh", exception.Message);
        Assert.Contains("--lambda-name", exception.Message);
    }

    [Fact]
    public async Task Build_FileAndLambdaNameBothGiven_Throws()
    {
        var specPath = WriteSpecFile();
        try
        {
            var payload = new BuildPayload
            {
                File = specPath,
                LambdaName = "some-lambda-func",
                Output = "client",
                Directory = Path.GetTempPath(),
            };

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => new ClientCodeBuilder().Build(payload));
            Assert.Contains("Multiple spec sources", exception.Message);
        }
        finally
        {
            File.Delete(specPath);
        }
    }

    [Fact]
    public async Task Build_FileAndUrlBothGiven_Throws()
    {
        var specPath = WriteSpecFile();
        try
        {
            var payload = new BuildPayload
            {
                File = specPath,
                Url = "https://orders.example.com",
                Output = "client",
                Directory = Path.GetTempPath(),
            };

            await Assert.ThrowsAsync<ArgumentException>(() => new ClientCodeBuilder().Build(payload));
        }
        finally
        {
            File.Delete(specPath);
        }
    }

    [Fact]
    public async Task Build_MeshWithoutService_Throws()
    {
        var payload = new BuildPayload
        {
            Mesh = "https://mesh.example.com/manifest.json",
            Output = "client",
            Directory = Path.GetTempPath(),
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => new ClientCodeBuilder().Build(payload));
        Assert.Contains("--service", exception.Message);
    }

    [Fact]
    public void CodeBuilderFactory_TopicClient_WithResolvedServiceName_NamesClientsFromTheDocument()
    {
        var document = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("order:create", typeof(CreateOrder), typeof(Order)),
        }.ToEventServiceDocument();

        var payload = new BuildPayload { Output = "topic-client", ServiceName = "Orders" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(document);

        Assert.Contains(files, f => f.Name == "OrderCreate/OrderCreateServiceClient.cs");
        Assert.Contains(files, f => f.Name == "OrderCreate/IOrderCreateServiceClient.cs");
    }

    [Fact]
    public void CodeBuilderFactory_WithoutServiceNameOverride_KeepsOriginalLambdaNameDerivation()
    {
        // No --service-name / --file / --url / --mesh default: CodeBuilderFactory must keep
        // deriving purely from --lambda-name, unchanged from before Phase 3 (regression guard for
        // every pre-existing --lambda-name call site) - "benzene-orders-func" ->
        // LambdaNameParser.GetServiceName == "BenzeneOrders", GetNamespace(..., "Client") ==
        // "Benzene.Orders.Client", exactly as LambdaNameParserTest already pins.
        var document = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("order:create", typeof(CreateOrder), typeof(Order)),
        }.ToEventServiceDocument();
        var payload = new BuildPayload { Output = "client", LambdaName = "benzene-orders-func" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(document);

        Assert.IsType<Benzene.CodeGen.Client.MessageClientSdkBuilder>(builder);
        var clientSource = string.Join("\n", files.Single(f => f.Name == "BenzeneOrdersServiceClient.cs").Lines);
        Assert.Contains("namespace Benzene.Orders.Client.BenzeneOrders", clientSource);
    }
}
