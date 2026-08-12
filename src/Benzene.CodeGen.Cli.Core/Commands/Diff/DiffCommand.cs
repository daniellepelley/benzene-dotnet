using Benzene.Schema.OpenApi.Compatibility;
using Benzene.Schema.OpenApi.EventService;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Benzene.CodeGen.Cli.Core.Commands.Diff;

/// <summary>
/// Compares two Benzene spec JSON files (e.g. a committed baseline and the current build's output,
/// both Phase 1's <c>{Service}.spec.json</c> artifact) for backward compatibility, wrapping
/// <see cref="SchemaCompatibilityComparer"/>. Intended for CI: exits non-zero when the report trips
/// the requested <c>--fail-on</c> threshold, so a breaking change fails the build.
/// </summary>
public class DiffCommand : CommandBase<DiffPayload>
{
    private static readonly string[] ValidFailOnValues = { "breaking", "warning", "none" };

    public DiffCommand()
        : base("diff", "Compares two Benzene spec JSON files for backward compatibility")
    { }

    public override async Task ExecuteAsync(DiffPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Baseline))
        {
            throw new ArgumentException("--baseline is required: path to the baseline spec JSON file");
        }

        if (string.IsNullOrWhiteSpace(payload.Current))
        {
            throw new ArgumentException("--current is required: path to the current spec JSON file");
        }

        var failOn = ResolveFailOn(payload);

        var baselineJson = await File.ReadAllTextAsync(payload.Baseline);
        var currentJson = await File.ReadAllTextAsync(payload.Current);

        var deserializer = new EventServiceDocumentDeserializer();
        var baseline = deserializer.Deserialize(baselineJson);
        var current = deserializer.Deserialize(currentJson);

        var report = SchemaCompatibility.Compare(baseline, current);

        if (string.Equals(payload.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(report);
        }
        else
        {
            WriteText(report);
        }

        if (Trips(report, failOn))
        {
            var breaking = report.Changes.Count(x => x.Compatibility == ChangeCompatibility.Breaking);
            var warning = report.Changes.Count(x => x.Compatibility == ChangeCompatibility.Warning);
            throw new DiffFailedException(report,
                $"benzene diff: {breaking} breaking, {warning} warning change(s) - failing on --fail-on {failOn}");
        }
    }

    private static string ResolveFailOn(DiffPayload payload)
    {
        if (string.Equals(payload.WarnOnly, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        var failOn = string.IsNullOrWhiteSpace(payload.FailOn) ? Constants.FailOnDefault : payload.FailOn;
        if (!ValidFailOnValues.Contains(failOn))
        {
            throw new ArgumentException(
                $"--fail-on must be one of 'breaking', 'warning' or 'none' (got '{failOn}')");
        }

        return failOn;
    }

    private static bool Trips(SchemaCompatibilityReport report, string failOn) => failOn switch
    {
        "breaking" => report.HasBreakingChanges,
        "warning" => report.HasBreakingChanges || report.HasWarnings,
        _ => false, // "none" (or --warn-only): report only, never fail.
    };

    private static void WriteText(SchemaCompatibilityReport report)
    {
        if (report.Changes.Count == 0)
        {
            Console.WriteLine("No changes - the current contract is identical to the baseline.");
            return;
        }

        foreach (var change in report.Changes)
        {
            Console.WriteLine(
                $"[{change.Compatibility}] {change.Kind} {change.Topic} {change.Path} ({change.Direction}): {change.Description}");
        }

        var breaking = report.Changes.Count(x => x.Compatibility == ChangeCompatibility.Breaking);
        var warning = report.Changes.Count(x => x.Compatibility == ChangeCompatibility.Warning);
        var compatible = report.Changes.Count(x => x.Compatibility == ChangeCompatibility.Compatible);

        Console.WriteLine();
        Console.WriteLine(
            $"Summary: {report.Changes.Count} change(s) - {breaking} breaking, {warning} warning, {compatible} compatible");
    }

    private static void WriteJson(SchemaCompatibilityReport report)
    {
        var json = JsonConvert.SerializeObject(report, Formatting.Indented, new StringEnumConverter());
        Console.WriteLine(json);
    }
}
