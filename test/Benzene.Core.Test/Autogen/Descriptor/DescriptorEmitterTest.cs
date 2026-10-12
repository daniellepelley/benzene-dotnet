using System;
using System.IO;
using System.Text.Json.Nodes;
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
/// <remarks>
/// Serial, in <see cref="ConsoleRedirectionCollection"/>, for two reasons. The example's
/// <c>UseMessageHandlers()</c> discovers handlers in every assembly loaded in the process, and other
/// tests load assemblies with handlers in them while the suite runs (<c>AwsQuickstartRunsTest</c>
/// compiles a docs snippet and loads it). Run alongside them, two emits in one test could see different
/// handlers, and so different hashes, which failed <c>Emit_IdentityAndPlacementAreOutsideBothHashes</c>
/// about one full run in five. And <see cref="DescriptorEmitter"/> swaps <c>Console.Out</c> while it
/// builds the document, which is process-wide too.
/// </remarks>
[Collection(ConsoleRedirectionCollection.Name)]
public class DescriptorEmitterTest
{
    private static string MinimalAssemblyPath =>
        typeof(Benzene.Examples.Aws.Minimal.StartUp).Assembly.Location;

    private static EmitOptions Options(string? output = null, string? startup = null, string? cloud = null,
        string? region = null, string? service = "minimal") => new()
    {
        AssemblyPath = MinimalAssemblyPath,
        ServiceName = service,
        ServiceVersion = "1.0.0",
        VersionScheme = "semver",
        OutputPath = output,
        StartupTypeName = startup,
        Cloud = cloud,
        Region = region,
    };

    [Fact]
    public void Emit_ProducesTheContractDocument_RoundTrippingThroughEventServiceDocumentDeserializer()
    {
        var result = DescriptorEmitter.Emit(Options());

        var doc = new EventServiceDocumentDeserializer().Deserialize(result.SpecJson);

        Assert.Equal(EventServiceDocument.SchemaDialectOpenApi30, doc.SchemaDialect);
        Assert.Contains(doc.Requests, r => r.Topic == "order:placed");
    }

    [Fact]
    public void Emit_CarriesTheIdentityAndDeploymentFieldsThisBuildWasTold()
    {
        // #171: --version-scheme used to be validated and then discarded. It now reaches the emitted
        // document's versionScheme, beside serviceVersion (contract-document.md §1).
        var result = DescriptorEmitter.Emit(Options(cloud: "aws", region: "eu-west-1"));

        var root = JsonNode.Parse(result.SpecJson)!.AsObject();
        Assert.Equal("minimal", root["service"]!.GetValue<string>());
        Assert.Equal("1.0.0", root["serviceVersion"]!.GetValue<string>());
        Assert.Equal("semver", root["versionScheme"]!.GetValue<string>());
        Assert.Equal("dotnet", root["runtime"]!.GetValue<string>());
        Assert.Equal("aws", root["placement"]!["cloud"]!.GetValue<string>());
        Assert.Equal("eu-west-1", root["placement"]!["region"]!.GetValue<string>());
        Assert.StartsWith("sha256:", root["contractHash"]!.GetValue<string>());
        Assert.StartsWith("sha256:", root["registrationHash"]!.GetValue<string>());
    }

    [Fact]
    public void Emit_IdentityAndPlacementAreOutsideBothHashes()
    {
        var plain = JsonNode.Parse(DescriptorEmitter.Emit(Options(service: null)).SpecJson)!.AsObject();
        var placed = JsonNode.Parse(DescriptorEmitter.Emit(Options(cloud: "gcp")).SpecJson)!.AsObject();

        Assert.Equal(plain["contractHash"]!.GetValue<string>(), placed["contractHash"]!.GetValue<string>());
        Assert.Equal(plain["registrationHash"]!.GetValue<string>(), placed["registrationHash"]!.GetValue<string>());
    }

    [Fact]
    public void Emit_WithoutACloud_EmitsNoPlacement_ABuildDoesNotGuessWhereItWillRun()
    {
        var root = JsonNode.Parse(DescriptorEmitter.Emit(Options()).SpecJson)!.AsObject();

        Assert.False(root.ContainsKey("placement"));
    }

    [Fact]
    public void Emit_IsNotTheRetiredServiceDescriptor()
    {
        var root = JsonNode.Parse(DescriptorEmitter.Emit(Options()).SpecJson)!.AsObject();

        // mesh.md §2 (spec-0.4.0): the register body is the contract document - no topics/produces,
        // no descriptorHash.
        Assert.False(root.ContainsKey("topics"));
        Assert.False(root.ContainsKey("produces"));
        Assert.False(root.ContainsKey("descriptorHash"));
        Assert.True(root.ContainsKey("requests"));
        Assert.True(root.ContainsKey("events"));
        Assert.True(root.ContainsKey("sends"));
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
        var result = DescriptorEmitter.Emit(Options(startup: "Benzene.Examples.Aws.Minimal.StartUp"));

        Assert.NotNull(result.SpecJson);
    }

    [Fact]
    public void Emit_UnknownStartupTypeName_Throws()
    {
        var bad = Options(startup: "Nonexistent.Type.Name");

        Assert.Throws<InvalidOperationException>(() => DescriptorEmitter.Emit(bad));
    }

    [Fact]
    public void ResolveOutputPath_NoOutput_DefaultsNextToTheAssembly()
    {
        var path = DescriptorEmitter.ResolveOutputPath(Options());

        var expectedBase = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(MinimalAssemblyPath))!,
            Path.GetFileNameWithoutExtension(MinimalAssemblyPath));

        Assert.Equal(expectedBase + ".spec.json", path);
    }

    [Fact]
    public void ResolveOutputPath_TreatsOutputAsTheExactPath()
    {
        Assert.Equal("/tmp/foo/custom.json", DescriptorEmitter.ResolveOutputPath(Options(output: "/tmp/foo/custom.json")));
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("both")]
    public void ValidateArtifacts_RejectsTheRetiredDescriptorEmits(string emit)
    {
        var opts = EmitOptions.Parse(["--assembly", "svc.dll", "--emit", emit]);

        Assert.NotNull(opts);
        Assert.Contains("contract document", opts.ValidateArtifacts());
    }

    [Fact]
    public void ValidateArtifacts_AcceptsSpec_AndRejectsARegionWithoutACloud()
    {
        Assert.Null(EmitOptions.Parse(["--assembly", "svc.dll", "--emit", "spec"])!.ValidateArtifacts());
        Assert.NotNull(EmitOptions.Parse(["--assembly", "svc.dll", "--region", "eu-west-1"])!.ValidateArtifacts());
    }

    // ── service-versions.md §2: version order is declared, and declared wrong fails the build ───────────────
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
        // service-versions.md §2 defines no default on purpose. A version with no declared comparison rule is an
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
        // service-versions.md §1 case 3: a service that declares no version has exactly one service version.
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
