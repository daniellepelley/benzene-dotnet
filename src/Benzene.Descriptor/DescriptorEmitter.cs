using System.Reflection;
using Benzene.Abstractions.Hosting;
using Benzene.Microsoft.Dependencies;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;

namespace Benzene.Descriptor;

/// <summary>
/// The contract artifact <see cref="DescriptorEmitter"/> produced from a built service: the contract
/// document as JSON, ready to write to disk or compare against a mesh artifact store.
/// </summary>
public sealed record DescriptorEmitResult(string SpecJson);

/// <summary>
/// Emits a service's contract document (contract-document.md) from a built, non-running Benzene
/// service - the document its <c>benzene:spec</c> topic serves and, with the deployment-side fields,
/// the body it registers with a mesh collector (mesh.md §2). One builder (<see cref="SpecBuilder"/>)
/// for all three. No deploy, no socket.
///
/// The mesh ServiceDescriptor (<c>{name}.service.json</c>) this tool used to emit beside it was retired
/// in spec-0.4.0: the contract document is the register body. (The older distilled deployment
/// projection - consumes/produces/transportKind - is deferred as a future <c>--emit deploy</c>; see the
/// 2026-08-12 owner design review, Amendment A, in
/// <c>work/archive/spec-mesh-tooling-implementation-plan-2026-08.md</c>.
/// <see cref="OutboundRouteInspector"/> is kept, unused for now, for when that lands.)
/// </summary>
public static class DescriptorEmitter
{
    /// <summary>
    /// Builds the contract document in-process. Callable directly (no process spawn) so tests can
    /// assert on the JSON without shelling out to <c>Program.cs</c>; <c>Program.cs</c> itself is a thin
    /// shell over this plus file I/O and exit codes.
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

        return new DescriptorEmitResult(BuildSpec(build.Resolver, assembly, options));
    }

    /// <summary>
    /// Where the contract document is written, per Amendment C's "next to build output,
    /// <c>--output</c> overridable" rule: <c>--output</c> is the exact path; without it,
    /// <c>&lt;AssemblyName&gt;.spec.json</c> next to the assembly.
    /// </summary>
    public static string ResolveOutputPath(EmitOptions options)
    {
        if (options.OutputPath is not null)
        {
            return options.OutputPath;
        }

        var fullPath = Path.GetFullPath(options.AssemblyPath);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? ".", Path.GetFileNameWithoutExtension(fullPath)) + ".spec.json";
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

    // Runs SpecBuilder directly against the built container - no host round-trip, no I/O. For the
    // neutral adapter this yields everything except the inbound transports (empty); for a host adapter
    // it also carries transports and validation-enriched schemas. It is the contract document
    // (SpecRequest type "benzene"), with the identity and deployment-side fields this build was told
    // (contract-document.md §1) - outside both hashes, so they never change them.
    private static string BuildSpec(Benzene.Abstractions.DI.IServiceResolver resolver, Assembly assembly, EmitOptions options)
    {
        var stdout = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            var builder = (EventServiceDocumentBuilder)new SpecBuilder().CreateBuilder(resolver, SpecBuilder.BenzeneType);
            var document = builder.Build();

            if (options.ServiceName is not null)
            {
                document.Service = options.ServiceName;
            }
            else if (string.IsNullOrEmpty(document.Service))
            {
                document.Service = assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(options.AssemblyPath);
            }

            if (!string.IsNullOrWhiteSpace(options.ServiceVersion))
            {
                document.ServiceVersion = options.ServiceVersion;
                document.VersionScheme = options.VersionScheme;
            }

            document.Runtime = ContractDocumentDeployment.DotNetRuntime;
            if (options.Cloud is not null)
            {
                document.Placement = new ContractDocumentPlacement(options.Cloud, options.Region);
            }

            return document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        }
        finally
        {
            Console.SetOut(stdout);
        }
    }
}
