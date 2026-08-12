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
        "[--emit spec|descriptor|both] [--service <name>] [--service-version <v>] " +
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
    public string? ServiceVersion { get; init; }
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
            region = null, host = null, emit = null, startup = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--assembly": assembly = Next(); break;
                case "--output": output = Next(); break;
                case "--service": service = Next(); break;
                case "--service-version": version = Next(); break;
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
            Cloud = cloud ?? "aws",
            Region = region ?? "eu-west-1",
            Host = host,
            Emit = resolvedEmit,
            StartupTypeName = startup,
        };
    }
}
