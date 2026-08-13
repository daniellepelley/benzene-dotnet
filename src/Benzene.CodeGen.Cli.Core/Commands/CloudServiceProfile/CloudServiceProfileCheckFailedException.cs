using Benzene.CloudService.Probe;

namespace Benzene.CodeGen.Cli.Core.Commands.CloudServiceProfile;

/// <summary>
/// Thrown by <see cref="CloudServiceProfileCheckCommand"/> when the probe report trips the
/// requested <c>--fail-on</c> threshold. The report has already been printed to stdout by the time
/// this is thrown; throwing is how it reaches Phase 2's CLI exit-code mechanism (commands signal
/// failure by throwing, and <c>Program.Main</c> turns any propagated exception into exit code 1) -
/// the same pattern as <c>Commands.Diff.DiffFailedException</c>.
/// </summary>
public class CloudServiceProfileCheckFailedException : Exception
{
    public CloudServiceProfileCheckFailedException(CloudServiceProbeReport report, string message)
        : base(message)
    {
        Report = report;
    }

    /// <summary>The full probe report that tripped the threshold.</summary>
    public CloudServiceProbeReport Report { get; }
}
