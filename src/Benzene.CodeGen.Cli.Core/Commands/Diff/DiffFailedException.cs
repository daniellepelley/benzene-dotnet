using Benzene.Schema.OpenApi.Compatibility;

namespace Benzene.CodeGen.Cli.Core.Commands.Diff;

/// <summary>
/// Thrown by <see cref="DiffCommand"/> when the compatibility report trips the requested
/// <c>--fail-on</c> threshold. This is a lower bar than "has breaking changes" when
/// <c>--fail-on warning</c> is requested, so it is distinct from
/// <see cref="SchemaCompatibilityException"/> (which always means "has breaking changes"). The
/// report has already been printed to stdout by the time this is thrown; throwing is how it reaches
/// Phase 2's CLI exit-code mechanism (commands signal failure by throwing, and
/// <c>Program.Main</c> turns any propagated exception into exit code 1).
/// </summary>
public class DiffFailedException : Exception
{
    public DiffFailedException(SchemaCompatibilityReport report, string message)
        : base(message)
    {
        Report = report;
    }

    /// <summary>The full comparison report that tripped the threshold.</summary>
    public SchemaCompatibilityReport Report { get; }
}
