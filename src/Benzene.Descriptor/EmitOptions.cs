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
        "Usage: benzene-descriptor --assembly <service.dll> [--output <path>] " +
        "[--emit spec|descriptor|both] [--service <name>] " +
        "[--service-version <v> --version-scheme <integer|semver|lexicographic>] " +
        "[--cloud <aws>] [--region <r>] [--host <neutral|aws-lambda>] [--startup <fullTypeName>]";

    private static readonly string[] ValidEmitValues = { "spec", "descriptor", "both" };

    public required string AssemblyPath { get; init; }

    /// <summary>
    /// Where to write output. For <c>--emit spec</c>/<c>descriptor</c> this is the exact file path
    /// (stdout if null). For <c>--emit both</c> this is treated as the *descriptor* path and the
    /// spec path is derived from it (see <see cref="DescriptorEmitter.ResolveOutputPaths"/>); if
    /// null, both files default next to the assembly, named after it.
    /// </summary>
    public string? OutputPath { get; init; }

    public required string ServiceName { get; init; }

    /// <summary>
    /// The immutable release identity for this build — mesh.md §2.4. Supplied by the pipeline (a build
    /// number, a tag, a run id) and never derived from the contract, because two builds may declare
    /// byte-identical contracts and still be different releases.
    /// </summary>
    public string? ServiceVersion { get; init; }

    /// <summary>
    /// How <see cref="ServiceVersion"/>'s values are compared — mesh.md §2.5. Required whenever a
    /// version is declared; there is no default.
    /// </summary>
    /// <remarks>
    /// The scheme is declared rather than inferred because <c>"10"</c> and <c>"9"</c> order one way as
    /// integers and the opposite way as strings. A tool that guessed would report a rollback as an
    /// upgrade, and would do so silently.
    /// </remarks>
    public string? VersionScheme { get; init; }
    public string Cloud { get; init; } = "aws";
    public string Region { get; init; } = "eu-west-1";

    /// <summary>Force a specific host adapter (e.g. "neutral" for the cloud-agnostic core); auto-selected if null.</summary>
    public string? Host { get; init; }

    /// <summary>"spec", "descriptor", or "both". Defaults to "both" (2026-08-12 owner design review, Amendment A).</summary>
    public string Emit { get; init; } = "both";

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

        var resolvedEmit = emit ?? "both";
        if (!ValidEmitValues.Contains(resolvedEmit)) return null;

        return new EmitOptions
        {
            AssemblyPath = assembly,
            OutputPath = output,
            // Default the service name to the assembly's simple name if not supplied.
            ServiceName = service ?? Path.GetFileNameWithoutExtension(assembly),
            ServiceVersion = version,
            VersionScheme = versionScheme,
            Cloud = cloud ?? "aws",
            Region = region ?? "eu-west-1",
            Host = host,
            Emit = resolvedEmit,
            StartupTypeName = startup,
        };
    }

    /// <summary>
    /// Checks the declared version against its declared scheme (mesh.md §2.5), returning an error
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
    /// Declaring a version without a scheme is an error rather than a default. §2.5 has no default on
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
                   + "(integer, semver or lexicographic). mesh.md §2.5 defines no default: without a "
                   + "scheme this build declares an identity, not a position in an order.";
        }

        if (!MeshVersionOrder.TryParseScheme(VersionScheme, out var scheme))
        {
            return $"--version-scheme '{VersionScheme}' is not one of integer, semver, lexicographic. "
                   + "The set is closed (mesh.md §2.5) so an unknown scheme is rejected rather than "
                   + "falling back to string comparison, which would be indistinguishable from a "
                   + "correct answer.";
        }

        return MeshVersionOrder.IsValid(scheme, ServiceVersion)
            ? null
            : $"--service-version '{ServiceVersion}' is not a valid "
              + $"{MeshVersionOrder.SchemeName(scheme)} version.";
    }
}
