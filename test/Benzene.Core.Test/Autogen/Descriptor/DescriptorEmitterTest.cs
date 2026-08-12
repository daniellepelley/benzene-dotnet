using System;
using System.IO;
using System.Text.Json;
using Benzene.Descriptor;
using Benzene.Schema.OpenApi.EventService;
using Xunit;

namespace Benzene.Test.Autogen.Descriptor;

/// <summary>
/// Drives <see cref="DescriptorEmitter"/> directly, in-process — no process spawn, per the plan's
/// "factor the core so it's callable in-process, keep Program.cs a thin shell" guidance. Points at
/// the real, already-built <c>Benzene.Examples.Aws.Minimal</c> assembly (a ProjectReference in
/// Benzene.Test.csproj guarantees it is built alongside this test project; its location resolves
/// reliably via reflection instead of a hard-coded bin/ path).
/// </summary>
public class DescriptorEmitterTest
{
    private static string MinimalAssemblyPath =>
        typeof(Benzene.Examples.Aws.Minimal.StartUp).Assembly.Location;

    private static EmitOptions Options(string emit = "both", string? output = null, string? startup = null) => new()
    {
        AssemblyPath = MinimalAssemblyPath,
        ServiceName = "minimal",
        ServiceVersion = "1.0.0",
        Emit = emit,
        OutputPath = output,
        StartupTypeName = startup,
    };

    [Fact]
    public void Emit_Both_ProducesTwoParseableArtifacts()
    {
        var result = DescriptorEmitter.Emit(Options("both"));

        Assert.NotNull(result.SpecJson);
        Assert.NotNull(result.DescriptorJson);

        // Both must be well-formed JSON.
        using var _ = JsonDocument.Parse(result.SpecJson!);
        using var __ = JsonDocument.Parse(result.DescriptorJson!);
    }

    [Fact]
    public void Emit_Spec_RoundTripsThroughEventServiceDocumentDeserializer()
    {
        var result = DescriptorEmitter.Emit(Options("spec"));

        Assert.NotNull(result.SpecJson);
        Assert.Null(result.DescriptorJson);

        var doc = new EventServiceDocumentDeserializer().Deserialize(result.SpecJson!);

        Assert.Contains(doc.Requests, r => r.Topic == "order:placed");
    }

    [Fact]
    public void Emit_Descriptor_IsTheMeshSection2ServiceDescriptor_NotTheOldDistilledProjection()
    {
        var result = DescriptorEmitter.Emit(Options("descriptor"));

        Assert.Null(result.SpecJson);
        Assert.NotNull(result.DescriptorJson);

        using var doc = JsonDocument.Parse(result.DescriptorJson!);
        var root = doc.RootElement;

        // Mesh §2 shape: service/serviceVersion/placement/topics[]/descriptorHash.
        Assert.Equal("minimal", root.GetProperty("service").GetString());
        Assert.Equal("1.0.0", root.GetProperty("serviceVersion").GetString());
        Assert.True(root.TryGetProperty("topics", out var topics));
        Assert.True(topics.GetArrayLength() > 0);
        Assert.StartsWith("sha256:", root.GetProperty("descriptorHash").GetString());

        // NOT the old distilled projection (descriptorVersion/consumes/produces/transportsResolved).
        Assert.False(root.TryGetProperty("descriptorVersion", out _));
        Assert.False(root.TryGetProperty("consumes", out _));
        Assert.False(root.TryGetProperty("produces", out _));
    }

    [Fact]
    public void Emit_BogusAssemblyPath_Throws()
    {
        var bad = new EmitOptions
        {
            AssemblyPath = "/no/such/assembly.dll",
            ServiceName = "minimal",
        };

        Assert.Throws<InvalidOperationException>(() => DescriptorEmitter.Emit(bad));
    }

    [Fact]
    public void Emit_ExplicitStartup_Works()
    {
        var result = DescriptorEmitter.Emit(Options("spec", startup: "Benzene.Examples.Aws.Minimal.StartUp"));

        Assert.NotNull(result.SpecJson);
    }

    [Fact]
    public void Emit_UnknownStartupTypeName_Throws()
    {
        var bad = Options("spec", startup: "Nonexistent.Type.Name");

        Assert.Throws<InvalidOperationException>(() => DescriptorEmitter.Emit(bad));
    }

    [Fact]
    public void ResolveOutputPaths_NoOutput_DefaultsNextToTheAssembly()
    {
        var (specPath, descriptorPath) = DescriptorEmitter.ResolveOutputPaths(Options("both"));

        var expectedBase = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(MinimalAssemblyPath))!,
            Path.GetFileNameWithoutExtension(MinimalAssemblyPath));

        Assert.Equal(expectedBase + ".spec.json", specPath);
        Assert.Equal(expectedBase + ".service.json", descriptorPath);
    }

    [Fact]
    public void ResolveOutputPaths_BothEmit_DerivesSpecPathFromServiceJsonOutput()
    {
        var (specPath, descriptorPath) = DescriptorEmitter.ResolveOutputPaths(
            Options("both", output: "/tmp/foo/bar.service.json"));

        Assert.Equal("/tmp/foo/bar.service.json", descriptorPath);
        Assert.Equal("/tmp/foo/bar.spec.json", specPath);
    }

    [Fact]
    public void ResolveOutputPaths_SingleEmit_TreatsOutputAsTheExactPath()
    {
        var (specPath, _) = DescriptorEmitter.ResolveOutputPaths(Options("spec", output: "/tmp/foo/custom.json"));
        Assert.Equal("/tmp/foo/custom.json", specPath);

        var (_, descriptorPath) = DescriptorEmitter.ResolveOutputPaths(Options("descriptor", output: "/tmp/foo/custom.json"));
        Assert.Equal("/tmp/foo/custom.json", descriptorPath);
    }
}
