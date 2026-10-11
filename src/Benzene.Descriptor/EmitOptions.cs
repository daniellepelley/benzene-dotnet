using Benzene.Mesh.Contracts;

namespace Benzene.Descriptor;

/// <summary>
/// Parsed <c>benzene-descriptor</c> command-line options. Public (not <c>internal</c>) so
/// <see cref="DescriptorEmitter"/>'s core can be driven in-process by tests without spawning the
/// tool as a separate process — <c>Program.cs</c> stays a thin argument-parsing/exit-code shell
/// around this and <see cref="DescriptorEmitter"/>.
/// </summary>
public sealed class EmitOptions
{
    public const string Usage =
        "Usage: benzene-descriptor --assembly <service.dll> [--output <path>] [--emit spec] " +
        "[--service <name>] [--service-version <v> --version-scheme <integer|semver|lexicographic>] " +
        "[--cloud <aws|azure|gcp|self-hosted|...> [--region <r>]] [--host <neutral|aws-lambda>] " +
        "[--startup <fullTypeName>]";

    /// <summary>The only artifact: the contract document.</summary>
    public const string SpecEmit = "spec";

    public required string AssemblyPath { get; init; }

    /// <summary>
    /// Where to write the contract document; if null, <c>&lt;AssemblyName&gt;.spec.json</c> next to the
    /// assembly (see <see cref="DescriptorEmitter.ResolveOutputPath"/>).
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// The document's <c>service</c>. Null keeps the service's own name (its <c>IApplicationInfo</c>),
    /// falling back to the assembly's simple name when the service sets none.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// The immutable release identity for this build - the document's <c>serviceVersion</c>
    /// (service-versions.md §1). Supplied by the pipeline (a build number, a tag, a run id) and never
    /// derived from the contract, because two builds may declare byte-identical contracts and still be
    /// different releases.
    /// </summary>
    public string? ServiceVersion { get; init; }

    /// <summary>
    /// How <see cref="ServiceVersion"/>'s values are compared - the document's <c>versionScheme</c>
    /// (service-versions.md §2). Required whenever a version is declared; there is no default.
    /// </summary>
    /// <remarks>
    /// The scheme is declared rather than inferred because <c>"10"</c> and <c>"9"</c> order one way as
    /// integers and the opposite way as strings. A tool that guessed would report a rollback as an
    /// upgrade, and would do so silently.
    /// </remarks>
    public string? VersionScheme { get; init; }

    /// <summary>
    /// The document's <c>placement.cloud</c> (contract-document.md §1), or null to emit no placement: a
    /// build-time artifact does not know where it will run unless it is told.
    /// </summary>
    public string? Cloud { get; init; }

    /// <summary>The document's <c>placement.region</c>; only with <see cref="Cloud"/>, and only where the platform documents one.</summary>
    public string? Region { get; init; }

    /// <summary>Force a specific host adapter (e.g. "neutral" for the cloud-agnostic core); auto-selected if null.</summary>
    public string? Host { get; init; }

    /// <summary>
    /// Always <see cref="SpecEmit"/>. <c>descriptor</c> and <c>both</c> were removed with the mesh
    /// ServiceDescriptor (spec-0.4.0): the mesh register body is the contract document (mesh.md §2).
    /// </summary>
    public string Emit { get; init; } = SpecEmit;

    /// <summary>
    /// Full type name of the service's <c>BenzeneStartUp</c>, required only when the assembly
    /// defines more than one candidate (see <see cref="DescriptorEmitter.FindStartUpType"/>).
    /// </summary>
    public string? StartupTypeName { get; init; }

    public static EmitOptions? Parse(string[] args)
    {
        string? assembly = null, output = null, service = null, version = null, cloud = null,
            region = null, host = null, emit = null, startup = null, versionScheme = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--assembly": assembly = Next(); break;
                case "--output": output = Next(); break;
                case "--service": service = Next(); break;
                case "--service-version": version = Next(); break;
                case "--version-scheme": versionScheme = Next(); break;
                case "--cloud": cloud = Next(); break;
                case "--region": region = Next(); break;
                case "--host": host = Next(); break;
                case "--emit": emit = Next(); break;
                case "--startup": startup = Next(); break;
                default: return null;
            }
        }

        if (string.IsNullOrWhiteSpace(assembly)) return null;

        return new EmitOptions
        {
            AssemblyPath = assembly,
            OutputPath = output,
            ServiceName = string.IsNullOrWhiteSpace(service) ? null : service,
            ServiceVersion = version,
            VersionScheme = versionScheme,
            Cloud = string.IsNullOrWhiteSpace(cloud) ? null : cloud,
            Region = string.IsNullOrWhiteSpace(region) ? null : region,
            Host = host,
            Emit = emit ?? SpecEmit,
            StartupTypeName = startup,
        };
    }

    /// <summary>Checks <see cref="Emit"/> and the placement flags, returning an error message or null.</summary>
    public string? ValidateArtifacts()
    {
        if (Emit != SpecEmit)
        {
            return Emit is "descriptor" or "both"
                ? $"--emit {Emit} is no longer supported: the mesh ServiceDescriptor was retired in spec-0.4.0 and a "
                  + "service registers its contract document (mesh.md §2), which --emit spec (the default) writes."
                : $"--emit '{Emit}' is not supported; the only artifact is the contract document (--emit spec).";
        }

        return Region != null && Cloud == null
            ? "--region was given without --cloud; a region places nothing on its own."
            : null;
    }

    /// <summary>
    /// Checks the declared version against its declared scheme (service-versions.md §2), returning an error
    /// message or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The build that declares a version is the cheapest place in the whole system to catch a mismatch.
    /// Carrying an invalid one and discovering it at comparison time means the wrong answer — an
    /// upgrade shown as a rollback, or a "latest" that is not — has already reached somebody deciding
    /// a deployment.
    /// </para>
    /// <para>
    /// Declaring a version without a scheme is an error rather than a default. service-versions.md §2 has no default on
    /// purpose: a version with no declared comparison rule is an identity, not a position in an order,
    /// and silently picking one for it would be a guess wearing a specification's clothes.
    /// </para>
    /// </remarks>
    public string? ValidateVersion()
    {
        var hasVersion = !string.IsNullOrWhiteSpace(ServiceVersion);
        var hasScheme = !string.IsNullOrWhiteSpace(VersionScheme);

        if (!hasVersion)
        {
            return hasScheme
                ? "--version-scheme was given without --service-version; a scheme orders nothing on its own."
                : null;
        }

        if (!hasScheme)
        {
            return $"--service-version '{ServiceVersion}' needs --version-scheme "
                   + "(integer, semver or lexicographic). service-versions.md §2 defines no default: without a "
                   + "scheme this build declares an identity, not a position in an order.";
        }

        if (!MeshVersionOrder.TryParseScheme(VersionScheme, out var scheme))
        {
            return $"--version-scheme '{VersionScheme}' is not one of integer, semver, lexicographic. "
                   + "The set is closed (service-versions.md §2) so an unknown scheme is rejected rather than "
                   + "falling back to string comparison, which would be indistinguishable from a "
                   + "correct answer.";
        }

        return MeshVersionOrder.IsValid(scheme, ServiceVersion)
            ? null
            : $"--service-version '{ServiceVersion}' is not a valid "
              + $"{MeshVersionOrder.SchemeName(scheme)} version.";
    }
}
