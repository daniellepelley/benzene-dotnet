using Benzene.CloudService.Probe;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Benzene.CodeGen.Cli.Core.Commands.CloudServiceProfile;

/// <summary>
/// CLI wrapper for the external live-probe checker (docs/specification/cloud-service-profile.md
/// §5, <c>Benzene.CloudService.Probe</c>): hits a running Benzene Cloud Service over plain HTTP and
/// independently assesses R1-R8 without trusting anything the service claims about itself. Unlike
/// <see cref="Commands.HealthCheck.HealthCheckCommand"/>/<see cref="Commands.Spec.SpecCommand"/>,
/// this talks to a plain HTTP URL rather than invoking AWS Lambda directly.
/// </summary>
/// <remarks>
/// Exits non-zero when the report trips the requested <c>--fail-on</c> threshold (Diff.DiffCommand's
/// pattern) - the CLI's own top-level exit-code mechanism only reacts to a thrown exception
/// (Program.cs), and this command previously never threw regardless of the probe's verdicts, so
/// <c>profile-check</c> always exited 0 even when every requirement came back NotSatisfied. That
/// made it unusable as a CI gate despite the tool-wide doc comment's claim.
/// </remarks>
public class CloudServiceProfileCheckCommand : CommandBase<CloudServiceProfileCheckPayload>
{
    private static readonly string[] ValidFailOnValues = { "not-satisfied", "inconclusive", "none" };

    public CloudServiceProfileCheckCommand()
        : base("profile-check", "Externally probes a Benzene Cloud Service's R1-R8 conformance over HTTP")
    { }

    public override async Task ExecuteAsync(CloudServiceProfileCheckPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Url))
        {
            throw new ArgumentException("--url is required: the base URL of the Benzene Cloud Service to probe");
        }

        var failOn = ResolveFailOn(payload);

        var options = new CloudServiceProbeOptions
        {
            SendTraceParentProbe = !string.Equals(payload.NoTraceParentProbe, "true", StringComparison.OrdinalIgnoreCase)
        };
        if (!string.IsNullOrWhiteSpace(payload.InvokePath))
        {
            options.InvokePath = payload.InvokePath;
        }
        if (!string.IsNullOrWhiteSpace(payload.SpecPath))
        {
            options.SpecPath = payload.SpecPath;
        }
        if (!string.IsNullOrWhiteSpace(payload.HealthPath))
        {
            options.HealthPath = payload.HealthPath;
        }

        using var httpClient = new HttpClient { BaseAddress = new Uri(payload.Url) };
        var report = await CloudServiceProbe.RunAsync(httpClient, options);

        if (string.Equals(payload.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(payload.Url, report);
        }
        else
        {
            WriteText(payload.Url, report);
        }

        if (Trips(report, failOn))
        {
            throw new CloudServiceProfileCheckFailedException(report,
                $"benzene profile-check: {report.NotSatisfied.Count} not satisfied, {report.Inconclusive.Count} inconclusive - failing on --fail-on {failOn}");
        }
    }

    private static string ResolveFailOn(CloudServiceProfileCheckPayload payload)
    {
        var failOn = string.IsNullOrWhiteSpace(payload.FailOn) ? Constants.ProfileCheckFailOnDefault : payload.FailOn;
        if (!ValidFailOnValues.Contains(failOn))
        {
            throw new ArgumentException(
                $"--fail-on must be one of 'not-satisfied', 'inconclusive' or 'none' (got '{failOn}')");
        }

        return failOn;
    }

    private static bool Trips(CloudServiceProbeReport report, string failOn) => failOn switch
    {
        "not-satisfied" => report.NotSatisfied.Count > 0,
        "inconclusive" => report.NotSatisfied.Count > 0 || report.Inconclusive.Count > 0,
        _ => false, // "none": report only, never fail.
    };

    private static void WriteText(string url, CloudServiceProbeReport report)
    {
        Console.WriteLine($"Cloud Service Profile probe: {url}");
        Console.WriteLine();
        foreach (var requirement in report.Requirements)
        {
            Console.WriteLine($"{requirement.Id,-4} {requirement.Verdict,-13} {requirement.Description}");
            Console.WriteLine($"     {requirement.Reason}");
        }

        var satisfiedCount = report.Requirements.Count(x => x.Verdict == CloudServiceProbeVerdict.Satisfied);
        var notSatisfied = report.NotSatisfied;
        var inconclusive = report.Inconclusive;
        Console.WriteLine();
        Console.WriteLine(
            $"Summary: {satisfiedCount}/{report.Requirements.Count} satisfied" +
            $", {notSatisfied.Count} not satisfied" + (notSatisfied.Count == 0 ? "" : $" [{string.Join(", ", notSatisfied)}]") +
            $", {inconclusive.Count} inconclusive" + (inconclusive.Count == 0 ? "" : $" [{string.Join(", ", inconclusive)}]"));
    }

    private static void WriteJson(string url, CloudServiceProbeReport report)
    {
        var json = JsonConvert.SerializeObject(new { url, report.Requirements, report.NotSatisfied, report.Inconclusive, report.IsFullyConformant },
            Formatting.Indented, new StringEnumConverter());
        Console.WriteLine(json);
    }
}
