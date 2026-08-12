using System.Reflection;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Messages;
using Benzene.Core.MessageHandlers;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.Schema.OpenApi;

namespace Benzene.Descriptor;

/// <summary>
/// The contract artifact(s) <see cref="DescriptorEmitter"/> produced from a built service, one or
/// both depending on <see cref="EmitOptions.Emit"/>. Both are wire-shape JSON text, ready to write to
/// disk or compare against a mesh artifact store.
/// </summary>
public sealed record DescriptorEmitResult(string? SpecJson, string? DescriptorJson);

/// <summary>
/// Emits a service's contract artifacts from a built, non-running Benzene service:
/// <c>spec</c> — the <c>EventServiceDocument</c> (codegen/gate input), and <c>descriptor</c> — the
/// mesh §2 <c>ServiceDescriptor</c> wire shape, exactly as <c>benzene:mesh:register</c> sends it
/// (<c>MeshJson.Serialize(MeshDescriptorFactory.Create(...))</c> — see <c>docs/specification/mesh.md</c>
/// §2 in the Benzene repo). No deploy, no socket.
///
/// (The older distilled deployment projection — consumes/produces/transportKind — is deferred as a
/// future <c>--emit deploy</c>; see the 2026-08-12 owner design review, Amendment A, in
/// <c>work/spec-mesh-tooling-implementation-plan.md</c>. <see cref="OutboundRouteInspector"/> is kept,
/// unused for now, for when that lands.)
/// </summary>
public static class DescriptorEmitter
{
    /// <summary>
    /// Builds the requested artifact(s) in-process. Callable directly (no process spawn) so tests
    /// can assert on the JSON without shelling out to <c>Program.cs</c>; <c>Program.cs</c> itself is
    /// a thin shell over this plus file I/O and exit codes.
    /// </summary>
    public static DescriptorEmitResult Emit(EmitOptions options)
    {
        var fullPath = Path.GetFullPath(options.AssemblyPath);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"Assembly not found: {fullPath}");

        var alc = new ServiceLoadContext(fullPath);
        var assembly = alc.LoadFromAssemblyPath(fullPath);

        CheckVersionSkew(assembly);

        var startUpType = FindStartUpType(assembly, options.StartupTypeName);
        var adapter = options.Host is { } forced
            ? HostAdapters.All.FirstOrDefault(a => a.Name == forced)
              ?? throw new InvalidOperationException($"Unknown host adapter '{forced}'. Known: {string.Join(", ", HostAdapters.All.Select(a => a.Name))}.")
            : HostAdapters.Select(assembly);
        var build = adapter.Build(startUpType);

        string? specJson = null;
        string? descriptorJson = null;

        if (options.Emit is "spec" or "both")
            specJson = BuildSpec(build.Resolver);

        if (options.Emit is "descriptor" or "both")
            descriptorJson = BuildMesh(assembly, options);

        return new DescriptorEmitResult(specJson, descriptorJson);
    }

    /// <summary>
    /// Resolves where each requested artifact should be written, per Amendment C's "next to build
    /// output, <c>--output</c> overridable" rule: no <c>--output</c> defaults both files next to the
    /// assembly, named after it (<c>&lt;AssemblyName&gt;.spec.json</c> / <c>.service.json</c>); a
    /// single-artifact <c>--emit</c> treats <c>--output</c> as that artifact's exact path; with
    /// <c>--emit both</c>, <c>--output</c> is treated as the *descriptor* path and the spec path is
    /// derived from it (<c>.service.json</c> → <c>.spec.json</c>, else <c>.spec.json</c> appended).
    /// </summary>
    public static (string? SpecPath, string? DescriptorPath) ResolveOutputPaths(EmitOptions options)
    {
        var wantsSpec = options.Emit is "spec" or "both";
        var wantsDescriptor = options.Emit is "descriptor" or "both";

        if (options.OutputPath is null)
        {
            var fullPath = Path.GetFullPath(options.AssemblyPath);
            var baseName = Path.Combine(
                Path.GetDirectoryName(fullPath) ?? ".",
                Path.GetFileNameWithoutExtension(fullPath));
            return (
                wantsSpec ? baseName + ".spec.json" : null,
                wantsDescriptor ? baseName + ".service.json" : null);
        }

        if (options.Emit == "spec") return (options.OutputPath, null);
        if (options.Emit == "descriptor") return (null, options.OutputPath);

        // "both": --output is the descriptor path; derive the spec path from it.
        var descriptorPath = options.OutputPath;
        var specPath = descriptorPath.EndsWith(".service.json", StringComparison.OrdinalIgnoreCase)
            ? descriptorPath[..^".service.json".Length] + ".spec.json"
            : descriptorPath + ".spec.json";
        return (specPath, descriptorPath);
    }

    // The README's documented ALC pinning risk: ServiceLoadContext always prefers the tool's own
    // already-loaded Benzene.Core over the service's copy (that's what keeps type identity intact
    // across the boundary), so a version-skewed service silently gets the TOOL's Benzene.Core rather
    // than its own — no load error, just a service running against an API surface it wasn't built
    // for, which can fail in confusing ways deep inside Configure/ConfigureServices. Catch it early:
    // compare the version the service assembly was *compiled against* (its own AssemblyRef metadata)
    // to the version the tool actually carries, and fail loudly if they differ.
    private static void CheckVersionSkew(Assembly serviceAssembly)
    {
        var expected = serviceAssembly.GetReferencedAssemblies()
            .FirstOrDefault(a => a.Name == "Benzene.Core");
        if (expected is null) return; // not directly referenced - best effort, nothing to compare

        var actual = typeof(Benzene.Core.BenzeneFailure).Assembly.GetName();
        if (expected.Version != actual.Version)
            throw new InvalidOperationException(
                $"Benzene version mismatch: the service was built against Benzene.Core {expected.Version}, " +
                $"but benzene-descriptor is pinned to Benzene.Core {actual.Version}. Align the tool version " +
                "with the service's Benzene version.");
    }

    internal static Type FindStartUpType(Assembly assembly, string? startupTypeName = null)
    {
        if (startupTypeName is not null)
        {
            var explicitType = assembly.GetType(startupTypeName)
                ?? throw new InvalidOperationException(
                    $"Type '{startupTypeName}' not found in {assembly.GetName().Name}.");
            if (!typeof(BenzeneStartUp).IsAssignableFrom(explicitType) || explicitType.IsAbstract
                || explicitType.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException(
                    $"'{startupTypeName}' is not a concrete, parameterless BenzeneStartUp.");
            return explicitType;
        }

        var candidates = assembly.GetTypes()
            .Where(t => typeof(BenzeneStartUp).IsAssignableFrom(t) && !t.IsAbstract
                        && t.GetConstructor(Type.EmptyTypes) != null)
            .ToArray();

        return candidates.Length switch
        {
            0 => throw new InvalidOperationException(
                $"No public parameterless BenzeneStartUp found in {assembly.GetName().Name}."),
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Multiple BenzeneStartUp types found ({string.Join(", ", candidates.Select(c => c.Name))}); " +
                "specify one with --startup <fullTypeName>.")
        };
    }

    // Runs SpecBuilder directly against the built container — no host round-trip, no I/O. For the
    // neutral adapter this yields everything except the inbound transports (empty); for a host adapter
    // it also carries transports and validation-enriched schemas. This is the EventServiceDocument
    // shape (SpecRequest type "benzene") — the same shape EventServiceDocumentDeserializer reads back.
    private static string BuildSpec(Benzene.Abstractions.DI.IServiceResolver resolver)
    {
        var stdout = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            return new SpecBuilder().CreateSpec(resolver, new SpecRequest("benzene", "json"));
        }
        finally
        {
            Console.SetOut(stdout);
        }
    }

    // The mesh §2 ServiceDescriptor wire shape, exactly as benzene:mesh:register sends it — no
    // distillation. See mesh.md §2 in the Benzene repo and conformance/mesh-descriptor-cases.json.
    private static string BuildMesh(Assembly assembly, EmitOptions options)
    {
        var definitions = new ReflectionMessageHandlersFinder(assembly).FindDefinitions();
        var descriptor = MeshDescriptorFactory.Create(
            new DefinitionsLookUp(definitions),
            new MeshServiceInfo(options.ServiceName, serviceVersion: options.ServiceVersion,
                instanceId: options.ServiceName,
                placement: new MeshPlacement { Cloud = options.Cloud, Region = options.Region }));
        return MeshJson.Serialize(descriptor);
    }

    private sealed class DefinitionsLookUp : IMessageHandlerDefinitionLookUp
    {
        private readonly IMessageHandlerDefinition[] _definitions;
        public DefinitionsLookUp(IMessageHandlerDefinition[] definitions) => _definitions = definitions;
        public IMessageHandlerDefinition? FindHandler(ITopic topic)
            => _definitions.FirstOrDefault(x => x.Topic.Id == topic.Id && x.Topic.Version == topic.Version);
        public IMessageHandlerDefinition[] GetAllHandlers() => _definitions;
    }
}
