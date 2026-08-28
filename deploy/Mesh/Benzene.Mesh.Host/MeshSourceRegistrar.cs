using System.Globalization;
using Benzene.Abstractions.DI;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Aws.S3;
using Benzene.Mesh.Azure.Blob;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Dispatch;
using Benzene.Mesh.Fleet.Aws.XRay;
using Benzene.Mesh.Fleet.Jaeger;
using Benzene.Mesh.Fleet.Tempo;
using Benzene.Mesh.GoogleCloud.Storage;
using Benzene.Mesh.Tracing.Tempo;
using Benzene.Mesh.Usage.ApplicationInsights;
using Benzene.Mesh.Usage.CloudWatch;

namespace Benzene.Mesh.Host;

/// <summary>
/// Maps a <c>mesh.json</c> section's <c>source</c>/<c>type</c> name to the matching Benzene package's
/// <c>Add*</c> call - the one place config schema v1's names are known. A plain <c>switch</c> over
/// lowercased names, not a reflection-driven registry: a typo'd name must fail loudly at startup naming
/// the valid values (a silent fallback to a default is the single worst failure mode this host can
/// ship), and a <c>switch</c> is the version of that check a reader can audit at 3am.
/// </summary>
/// <remarks>
/// <see cref="Startup"/> and <see cref="MeshConfigValidator"/> both call these methods - the same
/// registration calls back the running host and <c>--validate-config</c>, so the two cannot silently
/// disagree about what a config means. None of the registrations below make a network call by
/// themselves (every adapter package registers its cloud client lazily, via <c>TryAddSingleton</c> with
/// a factory), so calling them against a throwaway container - <c>--validate-config</c>'s whole
/// approach - needs no live credentials.
/// </remarks>
public static class MeshSourceRegistrar
{
    /// <summary>Valid <see cref="MeshArtifactStoreConfig.Type"/> values, in the case <c>mesh.json</c> should use.</summary>
    public static readonly string[] ValidArtifactStoreTypes = { "file", "s3", "azureBlob", "gcs" };

    /// <summary>Valid <see cref="MeshUsageSourceConfig.Source"/> values, in the case <c>mesh.json</c> should use.</summary>
    public static readonly string[] ValidUsageSources = { "cloudwatch", "applicationInsights" };

    /// <summary>Valid <see cref="MeshFleetConfig.Source"/> values, in the case <c>mesh.json</c> should use.</summary>
    public static readonly string[] ValidFleetSources = { "none", "xray", "tempo", "jaeger" };

    /// <summary>Valid <see cref="MeshTopologyConfig.Source"/> values, in the case <c>mesh.json</c> should use.</summary>
    public static readonly string[] ValidTopologySources = { "none", "tempo" };

    // #247/#248's bounds-check ceilings - "sane ceiling" values, not derived from any hard technical
    // limit, sized so a genuine operator need clears them by a wide margin while an obvious config
    // mistake (a units confusion, a stray extra zero) does not.
    private const int MaxPerMinuteCeiling = 100_000;
    private const int MaxResponseBytesCeiling = 10_000_000; // 10 MB
    private const int MaxCorrelationSearchLimitCeiling = 10_000;
    private const int MaxFleetSearchConcurrencyCeiling = 100;

    /// <summary>
    /// Registers the mesh aggregator against <paramref name="config"/>'s artifact store - the local
    /// filesystem (<see cref="MeshHostConfig.ArtifactRootDirectory"/>, the default) or a blob-storage
    /// backend named by <see cref="MeshArtifactStoreConfig.Type"/>. Must run before
    /// <see cref="RegisterTopology"/> - the topology adapter requires an already-registered
    /// <c>IMeshArtifactStore</c> to publish <c>topology.json</c> to.
    /// </summary>
    /// <exception cref="InvalidOperationException">An unknown <c>type</c>, or a required option is missing.</exception>
    public static void RegisterArtifactStore(IBenzeneServiceContainer services, MeshServiceRegistry registry, MeshHostConfig config)
    {
        var store = config.ArtifactStore;
        switch (store.Type.ToLowerInvariant())
        {
            case "file":
                services.AddMeshAggregator(registry, config.ArtifactRootDirectory);
                break;
            case "s3":
                {
                    var bucket = RequireOption(store.Options, "bucket", "artifact store", "s3");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    services.AddMeshAggregatorWithS3(registry, bucket, prefix);
                    break;
                }
            case "azureblob":
                {
                    var blobServiceUri = RequireOption(store.Options, "blobServiceUri", "artifact store", "azureBlob");
                    var container = RequireOption(store.Options, "container", "artifact store", "azureBlob");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    services.AddMeshAggregatorWithBlob(registry, new Uri(blobServiceUri), container, prefix);
                    break;
                }
            case "gcs":
                {
                    var bucket = RequireOption(store.Options, "bucket", "artifact store", "gcs");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    services.AddMeshAggregatorWithGcs(registry, bucket, prefix);
                    break;
                }
            default:
                throw Unknown("artifact store type", store.Type, ValidArtifactStoreTypes);
        }
    }

    /// <summary>Registers every configured usage source (<see cref="MeshHostConfig.Usage"/>) - zero or more.</summary>
    /// <exception cref="InvalidOperationException">An unknown <c>source</c>, or a required option is missing.</exception>
    public static void RegisterUsageSources(IBenzeneServiceContainer services, MeshUsageSourceConfig[] usage)
    {
        foreach (var entry in usage)
        {
            switch (entry.Source.ToLowerInvariant())
            {
                case "cloudwatch":
                    services.AddCloudWatchUsage(BuildCloudWatchOptions(entry.Options));
                    break;
                case "applicationinsights":
                    services.AddApplicationInsightsUsage(BuildApplicationInsightsOptions(entry.Options));
                    break;
                default:
                    throw Unknown("usage source", entry.Source, ValidUsageSources);
            }
        }
    }

    /// <summary>
    /// Registers <paramref name="fleet"/>'s source, if any - <c>"none"</c> (the default) registers
    /// nothing.
    /// </summary>
    /// <returns>
    /// Whether a live fleet plane was registered, so the caller knows whether to also wire the read
    /// handlers and point the mesh UI at the envelope (see <see cref="Startup"/>).
    /// </returns>
    /// <exception cref="InvalidOperationException">An unknown <c>source</c>, or a required option is missing.</exception>
    public static bool RegisterFleet(IBenzeneServiceContainer services, MeshFleetConfig fleet)
    {
        switch (fleet.Source.ToLowerInvariant())
        {
            case "none":
                return false;
            case "xray":
                services.AddXRayFleetReadModel(BuildXRayOptions(fleet.Options));
                return true;
            case "tempo":
                services.AddTempoFleetReadModel(BuildTempoTraceOptions(fleet.Options));
                return true;
            case "jaeger":
                services.AddJaegerFleetReadModel(BuildJaegerOptions(fleet.Options));
                return true;
            default:
                throw Unknown("fleet source", fleet.Source, ValidFleetSources);
        }
    }

    /// <summary>
    /// Registers <paramref name="topology"/>'s source, if any - <c>"none"</c> (the default) registers
    /// nothing. Requires <see cref="RegisterArtifactStore"/> to already be registered in the same
    /// container (<c>topology.json</c> is published to the same artifact store <c>manifest.json</c> is).
    /// </summary>
    /// <returns>Whether a topology source was registered.</returns>
    /// <exception cref="InvalidOperationException">An unknown <c>source</c>, or a required option is missing.</exception>
    public static bool RegisterTopology(IBenzeneServiceContainer services, MeshTopologyConfig topology)
    {
        switch (topology.Source.ToLowerInvariant())
        {
            case "none":
                return false;
            case "tempo":
                services.AddTempoTopology(BuildTempoTopologyOptions(topology.Options));
                return true;
            default:
                throw Unknown("topology source", topology.Source, ValidTopologySources);
        }
    }

    private static CloudWatchUsageOptions BuildCloudWatchOptions(Dictionary<string, string>? options)
    {
        var result = new CloudWatchUsageOptions(
            @namespace: GetOption(options, "namespace") ?? "Benzene/Mesh",
            timeWindow: TryGetHours(options, "windowHours"),
            metricName: GetOption(options, "metricName") ?? "benzene.messages.processed");
        ApplyIfPresent(options, "topicDimension", v => result.TopicDimension = v);
        ApplyIfPresent(options, "transportDimension", v => result.TransportDimension = v);
        ApplyIfPresent(options, "resultDimension", v => result.ResultDimension = v);
        ApplyIfPresent(options, "periodSeconds", v => result.PeriodSeconds = ParseInt(v, "periodSeconds", "cloudwatch usage source"));
        return result;
    }

    private static ApplicationInsightsUsageOptions BuildApplicationInsightsOptions(Dictionary<string, string>? options)
    {
        var workspaceId = RequireOption(options, "workspaceId", "usage source", "applicationInsights");
        var result = new ApplicationInsightsUsageOptions(
            workspaceId: workspaceId,
            timeWindow: TryGetHours(options, "windowHours"),
            metricName: GetOption(options, "metricName") ?? "benzene.messages.processed");
        ApplyIfPresent(options, "topicDimension", v => result.TopicDimension = v);
        ApplyIfPresent(options, "transportDimension", v => result.TransportDimension = v);
        ApplyIfPresent(options, "resultDimension", v => result.ResultDimension = v);
        return result;
    }

    private static XRayTraceSourceOptions BuildXRayOptions(Dictionary<string, string>? options)
    {
        var defaults = new XRayTraceSourceOptions();
        return new XRayTraceSourceOptions
        {
            CorrelationLookback = TryGetHours(options, "correlationLookbackHours") ?? defaults.CorrelationLookback,
            RecentFlowsLookback = TryGetHours(options, "recentFlowsLookbackHours") ?? defaults.RecentFlowsLookback,
            RecentFlowsServiceEnrichmentMax = options != null && options.TryGetValue("recentFlowsServiceEnrichmentMax", out var max)
                ? ParseInt(max, "recentFlowsServiceEnrichmentMax", "xray fleet source")
                : defaults.RecentFlowsServiceEnrichmentMax,
        };
    }

    private static TempoTraceSourceOptions BuildTempoTraceOptions(Dictionary<string, string>? options)
    {
        var url = RequireOption(options, "url", "fleet source", "tempo");
        var defaults = new TempoTraceSourceOptions(url);

        // #247/#248: SearchConcurrency/CorrelationSearchLimit existed on TempoTraceSourceOptions since
        // rounds 12-13 (WP-2, #188/#190) but were unreachable from mesh.json - fleet.options now carries
        // them, matching the same loose-dictionary shape every other fleet.options key already uses.
        var searchConcurrency = options != null && options.TryGetValue("searchConcurrency", out var concurrency)
            ? ParseInt(concurrency, "searchConcurrency", "tempo fleet source")
            : defaults.SearchConcurrency;
        ValidateFleetSearchConcurrencyCeiling(searchConcurrency, "tempo fleet source");

        var correlationSearchLimit = options != null && options.TryGetValue("correlationSearchLimit", out var limit)
            ? ParseInt(limit, "correlationSearchLimit", "tempo fleet source")
            : defaults.CorrelationSearchLimit;
        ValidateInRange(correlationSearchLimit, 1, MaxCorrelationSearchLimitCeiling, "tempo fleet source option 'correlationSearchLimit'");

        return new TempoTraceSourceOptions(url)
        {
            CorrelationLookback = TryGetHours(options, "correlationLookbackHours") ?? defaults.CorrelationLookback,
            RecentFlowsLookback = TryGetHours(options, "recentFlowsLookbackHours") ?? defaults.RecentFlowsLookback,
            SearchConcurrency = searchConcurrency,
            CorrelationSearchLimit = correlationSearchLimit,
        };
    }

    private static JaegerTraceSourceOptions BuildJaegerOptions(Dictionary<string, string>? options)
    {
        var url = RequireOption(options, "url", "fleet source", "jaeger");
        var defaults = new JaegerTraceSourceOptions(url);
        var services = GetOption(options, "services");

        // #247/#248: same unreachable-from-config gap as Tempo's SearchConcurrency, above.
        var searchConcurrency = options != null && options.TryGetValue("searchConcurrency", out var concurrency)
            ? ParseInt(concurrency, "searchConcurrency", "jaeger fleet source")
            : defaults.SearchConcurrency;
        ValidateFleetSearchConcurrencyCeiling(searchConcurrency, "jaeger fleet source");

        return new JaegerTraceSourceOptions(url)
        {
            Services = string.IsNullOrEmpty(services)
                ? null
                : services.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CorrelationLookback = TryGetHours(options, "correlationLookbackHours") ?? defaults.CorrelationLookback,
            RecentFlowsLookback = TryGetHours(options, "recentFlowsLookbackHours") ?? defaults.RecentFlowsLookback,
            SearchLimitPerService = options != null && options.TryGetValue("searchLimitPerService", out var limit)
                ? ParseInt(limit, "searchLimitPerService", "jaeger fleet source")
                : defaults.SearchLimitPerService,
            SearchConcurrency = searchConcurrency,
        };
    }

    /// <summary>
    /// #247/#248: maps <see cref="MeshDispatchConfig"/>'s optional guard-bound fields onto a fresh
    /// <see cref="MeshDispatchGuardOptions"/>, falling back to that type's own defaults when a field is
    /// left null (today's hardcoded-only behavior, preserved exactly), with bounds validation so a
    /// config mistake fails at startup/<c>--validate-config</c> rather than shipping a guard that
    /// doesn't actually guard (a negative limit, a request cap Kestrel would silently never deliver).
    /// Called by both <see cref="Startup"/> and <see cref="MeshConfigValidator"/> - the one-set-of-rules
    /// guarantee this class's own remarks describe.
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured value is out of its valid range.</exception>
    public static MeshDispatchGuardOptions BuildDispatchGuardOptions(MeshDispatchConfig dispatch)
    {
        var result = new MeshDispatchGuardOptions();

        if (dispatch.MaxRequestBytes.HasValue)
        {
            // Ceiling is MeshDispatchGuardOptions.DefaultMaxRequestBytes itself, not a larger number -
            // see MeshDispatchConfig.MaxRequestBytes' own remarks for why a value above it would be
            // silently unreachable (Program.cs pins Kestrel's own MaxRequestBodySize to that same
            // constant, host-wide, at process startup).
            ValidateInRange(dispatch.MaxRequestBytes.Value, 1, MeshDispatchGuardOptions.DefaultMaxRequestBytes, "dispatch.maxRequestBytes");
            result.MaxRequestBytes = dispatch.MaxRequestBytes.Value;
        }

        if (dispatch.MaxPerMinutePerIdentity.HasValue)
        {
            // 0 is a legitimate, documented value (MeshDispatchGuardOptions.MaxPerMinutePerIdentity's own
            // remarks: "0 disables the per-identity limit") - only reject negative/implausibly large.
            ValidateInRange(dispatch.MaxPerMinutePerIdentity.Value, 0, MaxPerMinuteCeiling, "dispatch.maxPerMinutePerIdentity");
            result.MaxPerMinutePerIdentity = dispatch.MaxPerMinutePerIdentity.Value;
        }

        if (dispatch.MaxPerMinutePerTarget.HasValue)
        {
            ValidateInRange(dispatch.MaxPerMinutePerTarget.Value, 0, MaxPerMinuteCeiling, "dispatch.maxPerMinutePerTarget");
            result.MaxPerMinutePerTarget = dispatch.MaxPerMinutePerTarget.Value;
        }

        return result;
    }

    /// <summary>
    /// #247/#248: resolves <see cref="HttpMeshServiceDispatcher.MaxResponseBytes"/>'s own
    /// unreachable-from-config bound - null (unset) keeps
    /// <see cref="HttpMeshServiceDispatcher.DefaultMaxResponseBytes"/>. Unlike
    /// <see cref="MeshDispatchConfig.MaxRequestBytes"/>, this isn't bounded by Kestrel (it caps a
    /// response read FROM a dispatched-to service, not a request INTO this host), so it has its own,
    /// independent ceiling.
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured value is out of its valid range.</exception>
    public static int ResolveMaxResponseBytes(MeshDispatchConfig dispatch)
    {
        if (!dispatch.MaxResponseBytes.HasValue)
        {
            return HttpMeshServiceDispatcher.DefaultMaxResponseBytes;
        }

        ValidateInRange(dispatch.MaxResponseBytes.Value, 1, MaxResponseBytesCeiling, "dispatch.maxResponseBytes");
        return dispatch.MaxResponseBytes.Value;
    }

    private static TempoTopologyOptions BuildTempoTopologyOptions(Dictionary<string, string>? options)
    {
        var prometheusUrl = RequireOption(options, "prometheusUrl", "topology source", "tempo");
        return new TempoTopologyOptions(prometheusUrl, TryGetMinutes(options, "windowMinutes"));
    }

    private static string RequireOption(Dictionary<string, string>? options, string key, string kind, string name)
    {
        var value = GetOption(options, key);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"{kind} '{name}' requires option '{key}'.");
        }
        return value;
    }

    private static string? GetOption(Dictionary<string, string>? options, string key)
        => options != null && options.TryGetValue(key, out var value) ? value : null;

    private static void ApplyIfPresent(Dictionary<string, string>? options, string key, Action<string> apply)
    {
        var value = GetOption(options, key);
        if (value != null)
        {
            apply(value);
        }
    }

    private static TimeSpan? TryGetHours(Dictionary<string, string>? options, string key)
    {
        var value = GetOption(options, key);
        return value == null ? null : TimeSpan.FromHours(ParseDouble(value, key));
    }

    private static TimeSpan? TryGetMinutes(Dictionary<string, string>? options, string key)
    {
        var value = GetOption(options, key);
        return value == null ? null : TimeSpan.FromMinutes(ParseDouble(value, key));
    }

    private static double ParseDouble(string value, string key)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"Option '{key}' must be a number; got '{value}'.");
        }
        return result;
    }

    private static int ParseInt(string value, string key, string source)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"{source} option '{key}' must be a whole number; got '{value}'.");
        }
        return result;
    }

    private static InvalidOperationException Unknown(string kind, string name, IReadOnlyList<string> validValues)
        => new($"Unknown {kind} '{name}'. Valid values: {string.Join(", ", validValues)}.");

    /// <summary>#247/#248: shared bounds check - throws naming <paramref name="description"/> when
    /// <paramref name="value"/> falls outside [<paramref name="min"/>, <paramref name="max"/>].</summary>
    private static void ValidateInRange(int value, int min, int max, string description)
    {
        if (value < min || value > max)
        {
            throw new InvalidOperationException($"{description} must be between {min} and {max}; got {value}.");
        }
    }

    /// <summary>
    /// #247/#248: a fleet trace source's <c>searchConcurrency</c> (Tempo/Jaeger) documents <c>0</c> or a
    /// negative value as a deliberate "run every fetch at once, unbounded" choice (see
    /// <see cref="TempoTraceSourceOptions.SearchConcurrency"/>/<see cref="JaegerTraceSourceOptions.SearchConcurrency"/>'s
    /// own remarks) - so unlike <see cref="ValidateInRange"/>, this never rejects a low value, only a
    /// positive one so large it is very likely a units mistake (e.g. confused with a byte or millisecond
    /// value) rather than a deliberate fan-out width a shared query backend could actually sustain.
    /// </summary>
    private static void ValidateFleetSearchConcurrencyCeiling(int value, string source)
    {
        if (value > MaxFleetSearchConcurrencyCeiling)
        {
            throw new InvalidOperationException(
                $"{source} option 'searchConcurrency' must be at most {MaxFleetSearchConcurrencyCeiling} " +
                $"(0 or a negative value means unbounded); got {value}.");
        }
    }
}
