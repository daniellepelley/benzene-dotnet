using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace Benzene.Mesh.Usage.ApplicationInsights;

/// <summary>
/// The default <see cref="IApplicationInsightsUsageQuery"/>: issues a KQL query against a Log Analytics
/// workspace (the store behind a workspace-based Application Insights resource), summing the
/// <c>customMetrics</c> counter by its <c>customDimensions</c>. Returns one <see cref="UsageCount"/> per
/// (topic, transport, result) combination over the window.
/// </summary>
public class LogsQueryUsageQuery : IApplicationInsightsUsageQuery
{
    private readonly LogsQueryClient _client;

    /// <summary>Initializes a new instance wrapping <paramref name="client"/>.</summary>
    /// <param name="client">The Azure Monitor logs-query client (authenticated for the workspace).</param>
    public LogsQueryUsageQuery(LogsQueryClient client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageCount>> QueryAsync(
        ApplicationInsightsUsageOptions options, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        // Sum the counter (customMetrics.valueSum is the per-interval sum; with delta temporality that is
        // the request delta, so a sum over the window is the total) grouped by the three tag dimensions.
        // The dimension keys are configurable; the result columns are fixed so parsing stays stable. The
        // configured values are config-time (not live caller input), but escaped anyway for defence-in-depth
        // - see EscapeKqlStringLiteral - the same posture XRayTraceSource.Escape applies to its filter value.
        var kql =
            $"customMetrics\n" +
            $"| where name == \"{EscapeKqlStringLiteral(options.MetricName, nameof(options.MetricName))}\"\n" +
            $"| extend _topic = tostring(customDimensions[\"{EscapeKqlStringLiteral(options.TopicDimension, nameof(options.TopicDimension))}\"]),\n" +
            $"         _transport = tostring(customDimensions[\"{EscapeKqlStringLiteral(options.TransportDimension, nameof(options.TransportDimension))}\"]),\n" +
            $"         _result = tostring(customDimensions[\"{EscapeKqlStringLiteral(options.ResultDimension, nameof(options.ResultDimension))}\"])\n" +
            $"| summarize _count = sum(valueSum) by _topic, _transport, _result";

        var response = await _client.QueryWorkspaceAsync(
            options.WorkspaceId, kql, new QueryTimeRange(startUtc, endUtc), cancellationToken: cancellationToken);

        var table = response.Value.Table;
        var rows = new List<UsageCount>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var count = (long)Math.Round(row.GetDouble("_count") ?? 0d);
            rows.Add(new UsageCount(
                Topic: NullIfEmpty(row.GetString("_topic")),
                Transport: NullIfEmpty(row.GetString("_transport")),
                Result: NullIfEmpty(row.GetString("_result")),
                Count: count));
        }

        return rows;
    }

    // KQL tostring of a missing customDimensions key yields an empty string; treat that as an absent
    // dimension (null), never a real "" value - the same missing-dimension honesty the feed requires.
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Makes a configured value (metric name / dimension key) safe to interpolate into a
    /// double-quoted KQL string literal. These come from <see cref="ApplicationInsightsUsageOptions"/>
    /// (config-time, not live caller input), so this is defence-in-depth rather than a live injection
    /// fix - but it's cheap and matches the escaping <c>XRayTraceSource.Escape</c> already applies to its
    /// (also config/annotation-derived) filter value. A line break can't occur in a legitimate metric or
    /// dimension name and would let a misconfigured value inject a whole extra KQL statement, so it's
    /// rejected outright rather than escaped; backslashes and quotes are escaped so the value stays inside
    /// its literal. Internal (not private) + <c>InternalsVisibleTo</c> so the test project can verify the
    /// escaping directly, without mocking Azure.Monitor.Query's SDK-internal <c>Response&lt;T&gt;</c>.</summary>
    internal static string EscapeKqlStringLiteral(string value, string paramName)
    {
        if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                $"Application Insights usage query option '{paramName}' must not contain line breaks.", paramName);
        }

        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
