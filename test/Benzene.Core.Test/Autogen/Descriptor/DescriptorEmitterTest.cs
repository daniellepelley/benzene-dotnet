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

        // Mesh §2 shape: service/serviceVersion/placement/topics[]/consumes[]/descriptorHash.
        Assert.Equal("minimal", root.GetProperty("service").GetString());
        Assert.Equal("1.0.0", root.GetProperty("serviceVersion").GetString());
        Assert.True(root.TryGetProperty("topics", out var topics));
        Assert.True(topics.GetArrayLength() > 0);
        Assert.StartsWith("sha256:", root.GetProperty("descriptorHash").GetString());

        // §2.3's `consumes` IS the mesh §2 shape (2026-08 revision) - present, but empty and honestly
        // degraded: this offline, no-running-service tool has no outbound registration to read (spec
        // §2.3 forbids inferring it from assembly reflection/call-site scanning), so it marks the gap
        // rather than asserting "consumes nothing".
        Assert.True(root.TryGetProperty("consumes", out var consumes));
        Assert.Equal(0, consumes.GetArrayLength());
        Assert.True(root.TryGetProperty("degraded", out var degraded));
        Assert.Contains(degraded.EnumerateArray(), d => d.GetString() == "outbound-registry");

        // NOT the OLD, unrelated distilled deployment projection (descriptorVersion/produces/
        // transportsResolved - a completely different, now-deferred `--emit deploy` shape; see
        // DescriptorEmitter's own doc comment).
        Assert.False(root.TryGetProperty("descriptorVersion", out _));
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

    // ── mesh.md §2.5: version order is declared, and declared wrong fails the build ───────────────
    //
    // The build that declares a version is the cheapest place in the system to catch a mismatch.
    // After here the value travels: into a catalogue, into a comparison, and onto a screen where
    // somebody decides a deployment from it. An upgrade shown as a rollback is the failure this
    // guards, and it is silent without these checks.

    private static EmitOptions Versioned(string? version, string? scheme) => new()
    {
        AssemblyPath = MinimalAssemblyPath,
        ServiceName = "minimal",
        ServiceVersion = version,
        VersionScheme = scheme,
    };

    [Theory]
    [InlineData("42", "integer")]
    [InlineData("1.3.0", "semver")]
    [InlineData("1.3.0-rc.1", "semver")]
    [InlineData("2026-08-16T09-00", "lexicographic")]
    public void ValidateVersion_AcceptsAValueThatParsesUnderItsDeclaredScheme(string version, string scheme)
    {
        Assert.Null(Versioned(version, scheme).ValidateVersion());
    }

    [Theory]
    [InlineData("1.3.0", "integer")]
    [InlineData("42", "semver")]
    [InlineData("v1.3.0", "semver")]
    [InlineData("-1", "integer")]
    public void ValidateVersion_RejectsAValueThatDoesNotParseUnderItsDeclaredScheme(string version, string scheme)
    {
        Assert.NotNull(Versioned(version, scheme).ValidateVersion());
    }

    [Fact]
    public void ValidateVersion_RequiresASchemeWheneverAVersionIsDeclared()
    {
        // §2.5 defines no default on purpose. A version with no declared comparison rule is an
        // identity, not a position in an order, and picking a rule for it silently would be a guess
        // wearing a specification's clothes.
        var error = Versioned("42", null).ValidateVersion();

        Assert.NotNull(error);
        Assert.Contains("--version-scheme", error);
    }

    [Fact]
    public void ValidateVersion_RejectsAnUnknownScheme()
    {
        // The set is closed. Falling back to string comparison would be indistinguishable from a
        // correct answer, which is exactly what makes it dangerous.
        var error = Versioned("2026.08", "calver").ValidateVersion();

        Assert.NotNull(error);
        Assert.Contains("closed", error);
    }

    [Fact]
    public void ValidateVersion_RejectsASchemeWithNoVersion()
    {
        Assert.NotNull(Versioned(null, "integer").ValidateVersion());
    }

    [Fact]
    public void ValidateVersion_IsSilentWhenNoVersionIsDeclaredAtAll()
    {
        // mesh.md §2.4 case 3: a service that declares no version has exactly one service version.
        // That is not an error and must not be reported as one — including here.
        Assert.Null(Versioned(null, null).ValidateVersion());
    }

    [Fact]
    public void Parse_ReadsTheVersionSchemeFlag()
    {
        var opts = EmitOptions.Parse(["--assembly", "svc.dll", "--service-version", "42", "--version-scheme", "integer"]);

        Assert.NotNull(opts);
        Assert.Equal("42", opts.ServiceVersion);
        Assert.Equal("integer", opts.VersionScheme);
        Assert.Null(opts.ValidateVersion());
    }
}
