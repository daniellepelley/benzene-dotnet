namespace Benzene.CodeGen.Cli.Core.Commands.HealthCheck;

/// <summary>
/// Thrown by <see cref="HealthCheckCommand"/> when the target's health check response reports
/// <c>isHealthy: false</c> and the requested <c>--fail-on</c> threshold is not <c>none</c>. The
/// response body has already been printed to stdout by the time this is thrown; throwing is how it
/// reaches Phase 2's CLI exit-code mechanism (commands signal failure by throwing, and
/// <c>Program.Main</c> turns any propagated exception into exit code 1) - the same pattern as
/// <c>Commands.Diff.DiffFailedException</c> / <c>Commands.CloudServiceProfile.CloudServiceProfileCheckFailedException</c>.
/// </summary>
public class HealthCheckFailedException : Exception
{
    public HealthCheckFailedException(string responseJson, string message)
        : base(message)
    {
        ResponseJson = responseJson;
    }

    /// <summary>The raw health check response body that tripped the threshold.</summary>
    public string ResponseJson { get; }
}
