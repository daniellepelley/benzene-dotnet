using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Wire;
using Benzene.Results;

namespace Benzene.Mesh.Collector;

/// <summary>
/// The in-memory state behind the spec collector (docs/specification/mesh.md §4-§6):
/// cumulative per-service and per-topic stats, the latest heartbeat per instance, registered
/// contract documents (mesh.md §2 - the SOLE source of the producer/consumer graph, §4), and a bounded ring of recent
/// trace events (the trace query and the additive §4.2 observed-liveness/drift signals derive
/// from). Everything is derived - a service that never registered still appears once its traces do
/// (anonymous but live, with its missing feeds named), a registered service with no traffic is a
/// catalog entry with its full declared graph and no stats, and no missing feed ever fails
/// ingestion or a query: the §6 degradation rule, collector side.
/// </summary>
public class MeshCollectorStore : IMeshFleetReadModel
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly int _maxIssues;
    private readonly int _maxVersionsPerService;
    private long _versionSequence;
    private readonly Dictionary<string, ServiceState> _services = new();
    private readonly Dictionary<(string Id, string Version), TopicState> _topics = new();
    private readonly Dictionary<string, MeshIssue> _issues = new();
    private readonly List<MeshTraceEvent> _ring;
    private int _next;

    // §4.2 (declared vs. observed) bookkeeping — additive read-model signals layered on the declared
    // graph above, NEVER fed back into it. spanService is a bounded parent-lookup index (evicted in
    // lockstep with the ring it shadows) used only to find who called a topic; it plays no role in
    // Providers/Consumers membership. providerActivity/consumerActivity record when a declared edge
    // was last actually exercised ("Unobserved" is the read model's job to report from these, not a
    // stored boolean per edge).
    private readonly Dictionary<string, string> _spanService = new();
    private readonly Dictionary<(string Id, string Version), Dictionary<string, DateTimeOffset>> _providerActivity = new();
    private readonly Dictionary<(string Id, string Version), Dictionary<string, DateTimeOffset>> _consumerActivity = new();

    /// <summary>The fixed fingerprint discriminator for a collector-synthesized "Undeclared" drift
    /// issue (spec §4.2/§4.1): unlike an emitter-reported issue, drift here isn't about one
    /// invocation's outcome (there is no meaningful exceptionType/status to key off), so identity is
    /// keyed on the edge alone - stable regardless of which status the exercising call happened to carry.</summary>
    private const string UndeclaredEdgeDiscriminator = "undeclared-edge";

    private const int MaxFleetTraces = 20;

    /// <param name="maxVersionsPerService">The retention cap on how many distinct
    /// <c>(service, serviceVersion)</c> registrations (#251) a single service name may hold at once
    /// (#290) - side-by-side deployments realistically hold 2-3 live versions; 8 gives generous
    /// headroom without accumulating one permanent entry per historical deploy for the life of a
    /// long-running collector process. See <see cref="EvictOneVersion"/>.</param>
    public MeshCollectorStore(int maxTraceEvents = 4096, int maxIssues = 1024, int maxVersionsPerService = 8)
    {
        _capacity = maxTraceEvents;
        _maxIssues = maxIssues;
        _maxVersionsPerService = maxVersionsPerService;
        _ring = new List<MeshTraceEvent>(Math.Min(maxTraceEvents, 1024));
    }

    /// <summary>
    /// When this store started accumulating - the window start for anything reporting the
    /// cumulative stats (storage is in-memory, so counts always cover "since process start").
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    private class ServiceState
    {
        /// <summary>Every currently live <c>(service, serviceVersion)</c> registration, keyed by
        /// <c>ServiceVersion ?? ""</c> - the catalog key mesh.md §4 gives a collector implementing Service
        /// Versions (<c>versionScheme</c> orders versions; it is not part of the key): two releases
        /// deployed side by side (a canary/blue-green) are two independent entries here, neither evicting
        /// the other. A re-registration of the SAME key replaces that key's entry wholesale; a DIFFERENT
        /// key is added alongside, never removing a sibling.</summary>
        public readonly Dictionary<string, MeshRegistration> Registrations = new();

        /// <summary>Monotonic registration-order stamp per version key (#290), set every time
        /// <see cref="Registrations"/> gains or refreshes that key - what makes "least-recently-
        /// registered" well-defined for <see cref="EvictOneVersion"/>'s eviction ordering.</summary>
        public readonly Dictionary<string, long> RegisteredAt = new();
        public readonly Dictionary<string, InstanceState> Instances = new();
        public DateTimeOffset LastSeen;
        public long Invocations;
        public long Errors;
        // True once ANY mesh:issues batch (including an empty liveness batch) named this service —
        // what lets "quiet wired feed" be distinguished from "feed not wired" (spec §4.1).
        public bool IssueFeedSeen;

        /// <summary>The most recently registered version's key. The service-NAME-level summary row
        /// (<see cref="ServiceSummary"/>/<see cref="ServiceView"/>'s scalar Runtime/Binding/Placement/
        /// Topics/ServiceVersion/Registration fields) reports THIS version, preserving the
        /// one-row-per-name shape for every caller that queries by name alone. Older still-live
        /// versions remain fully present in <see cref="Registrations"/> (and in the topic catalog's
        /// Providers/Consumers) for hash comparison and the declared graph - they're just not the
        /// name-level "headline" row.</summary>
        public string? CurrentVersionKey;

        public MeshRegistration? Registration =>
            CurrentVersionKey != null && Registrations.TryGetValue(CurrentVersionKey, out var r) ? r : null;
    }

    private class InstanceState
    {
        public bool Healthy;
        public DateTimeOffset LastHeartbeat;
        /// <summary>The heartbeat's <c>serviceVersion</c> key (<c>""</c> when it declared none) - which
        /// registration its hash is compared with (mesh.md §5).</summary>
        public string VersionKey = string.Empty;
        public string? ServiceVersion;
        public string? RegistrationHash;
    }

    private class TopicState
    {
        public readonly HashSet<string> Providers = new();
        public readonly HashSet<string> Consumers = new();
        public readonly Dictionary<string, long> StatusCounts = new();
        public long Invocations;
        public long Errors;
        public double TotalDurationMs;
        public DateTimeOffset LastSeen;
    }

    /// <summary>Stores the registered contract document (mesh.md §2) as this <c>(service,
    /// serviceVersion)</c>'s current contract, replacing any previous registration of the same key
    /// wholesale - a redeploy that drops a topic from <c>requests</c> drops the consumer claim with it,
    /// and one that drops a topic from <c>events</c> or <c>sends</c> drops the provider claim with it,
    /// symmetrically (spec §4). This - <c>requests</c> for CONSUMERS, <c>events</c> ∪ <c>sends</c> for
    /// PROVIDERS - is the SOLE source of the producer/consumer graph: trace parentage never admits or
    /// removes an edge here, only feeds the separate observed signals in
    /// <see cref="RecordObservedActivityAndDrift"/>.</summary>
    /// <remarks>
    /// Only <c>service</c> is required (mesh.md §2); the caller has checked it. A member that is absent
    /// or named in <c>degraded</c> is unknown and contributes no edges, and <c>benzene:</c> entries are
    /// ignored (<see cref="MeshRegistration.KnownEntries"/>). The carried <c>registrationHash</c> is
    /// trusted as is - never recomputed, never a reason to reject.
    /// </remarks>
    public void Register(MeshRegistration registration)
    {
        var versionKey = registration.ServiceVersion ?? string.Empty;

        lock (_lock)
        {
            var state = EnsureService(registration.Service);

            // Retract only what THIS version previously declared, not every topic the service name
            // has ever touched (spec §4): a re-registration of the SAME (service, version) pair
            // replaces its edges wholesale; a DIFFERENT version registering alongside it must never
            // evict a still-live sibling version's edges - two versions producing/consuming different
            // topics side by side is the expected canary/blue-green state.
            if (state.Registrations.ContainsKey(versionKey))
            {
                RetractEdges(state, versionKey, registration.Service);
            }
            else if (state.Registrations.Count >= _maxVersionsPerService)
            {
                // versionKey is a brand-new key, not yet in Registrations, and is about to become
                // CurrentVersionKey below - so it is never itself an eviction candidate here, and
                // the OLD current version's protection lapses the instant its successor registers
                // (#290: it stops being this service's headline the moment this call completes).
                EvictOneVersion(state, registration.Service);
            }

            state.Registrations[versionKey] = registration;
            state.RegisteredAt[versionKey] = ++_versionSequence;
            state.CurrentVersionKey = versionKey;
            state.LastSeen = DateTimeOffset.UtcNow;

            // requests -> Consumers; events ∪ sends -> Providers. Handling a topic makes you its
            // consumer; publishing an event on it or sending to it makes you its provider.
            foreach (var key in ConsumedKeys(registration))
            {
                EnsureTopic(key).Consumers.Add(registration.Service);
            }
            foreach (var key in ProvidedKeys(registration))
            {
                EnsureTopic(key).Providers.Add(registration.Service);
            }
        }
    }

    private static (string Id, string Version) Key(MeshRegisteredEntry entry) => (entry.Topic, entry.Version ?? string.Empty);

    /// <summary>The topics a registration consumes: its known <c>requests</c>.</summary>
    private static IEnumerable<(string Id, string Version)> ConsumedKeys(MeshRegistration registration)
        => registration.KnownEntries(MeshRegistrationMembers.Requests).Select(Key).Distinct();

    /// <summary>The topics a registration provides: its known <c>events</c> ∪ <c>sends</c>.</summary>
    private static IEnumerable<(string Id, string Version)> ProvidedKeys(MeshRegistration registration)
        => registration.KnownEntries(MeshRegistrationMembers.Events)
            .Concat(registration.KnownEntries(MeshRegistrationMembers.Sends))
            .Select(Key).Distinct();

    /// <summary>Removes the provider/consumer edges one specific service+version's prior registration
    /// (<paramref name="versionKey"/>) contributed, leaving every other service's edges - and every
    /// edge another still-live version of the SAME service declares - on the shared
    /// <see cref="TopicState"/> untouched.</summary>
    /// <remarks>
    /// The edge sets hold service NAMES, because graph membership is by service, not by version (spec
    /// §4): two live versions declaring one topic contribute one edge. So retracting one version must
    /// not remove a name a sibling version still declares - re-registering v1 without a topic would
    /// otherwise erase the edge v2 keeps declaring, which §4 forbids ("MUST NOT disturb a different
    /// service version of the same service"). Called before <paramref name="versionKey"/>'s entry is
    /// replaced or removed, so the sibling check excludes it explicitly.
    /// </remarks>
    private void RetractEdges(ServiceState state, string versionKey, string service)
    {
        var retracted = state.Registrations[versionKey];
        var siblings = state.Registrations.Where(x => x.Key != versionKey).Select(x => x.Value).ToList();

        foreach (var key in ConsumedKeys(retracted))
        {
            if (_topics.TryGetValue(key, out var topicState) && !siblings.Any(r => ConsumedKeys(r).Contains(key)))
            {
                topicState.Consumers.Remove(service);
            }
        }
        foreach (var key in ProvidedKeys(retracted))
        {
            if (_topics.TryGetValue(key, out var topicState) && !siblings.Any(r => ProvidedKeys(r).Contains(key)))
            {
                topicState.Providers.Remove(service);
            }
        }
    }

    /// <summary>Evicts one version from <paramref name="state"/>'s <see cref="ServiceState.Registrations"/>
    /// (#290) when a brand-new version registration would otherwise push the service over
    /// <see cref="_maxVersionsPerService"/> - without the cap the map would hold one permanent entry per
    /// historical <c>serviceVersion</c> for the life of a long-running collector process. Preference
    /// order: the least-recently-registered version (by <see cref="ServiceState.RegisteredAt"/>) that
    /// has NO live instance currently heartbeating it (<see cref="HasLiveInstance"/>); if every
    /// retained version has one, the cap still wins and the least-recently-registered version is
    /// evicted regardless - this is a bounded in-memory diagnostic store, not a health signal. The
    /// evicted version's provider/consumer edges are retracted (<see cref="RetractEdges"/>) exactly as
    /// a same-key re-registration does, so nothing is left dangling in the topic catalog.</summary>
    private void EvictOneVersion(ServiceState state, string service)
    {
        string? deadVictim = null;
        var deadVictimOrder = long.MaxValue;
        string? anyVictim = null;
        var anyVictimOrder = long.MaxValue;

        foreach (var key in state.Registrations.Keys)
        {
            var order = state.RegisteredAt.GetValueOrDefault(key, 0);
            if (order < anyVictimOrder)
            {
                anyVictim = key;
                anyVictimOrder = order;
            }
            if (!HasLiveInstance(state, key) && order < deadVictimOrder)
            {
                deadVictim = key;
                deadVictimOrder = order;
            }
        }

        var evictKey = deadVictim ?? anyVictim;
        if (evictKey == null)
        {
            return; // nothing to evict (e.g. Registrations is empty)
        }

        RetractEdges(state, evictKey, service);
        state.Registrations.Remove(evictKey);
        state.RegisteredAt.Remove(evictKey);
    }

    /// <summary>Whether any currently reporting instance heartbeats this version (its heartbeat's
    /// <c>serviceVersion</c> is <paramref name="versionKey"/>) - #290's eviction preference.</summary>
    private static bool HasLiveInstance(ServiceState state, string versionKey)
        => state.Instances.Values.Any(i => i.VersionKey == versionKey);

    /// <summary>Records the latest health report for one instance.</summary>
    public void Heartbeat(MeshHeartbeat heartbeat)
    {
        lock (_lock)
        {
            var state = EnsureService(heartbeat.Service);
            state.LastSeen = DateTimeOffset.UtcNow;
            state.Instances[heartbeat.InstanceId ?? string.Empty] = new InstanceState
            {
                Healthy = heartbeat.Health?.IsHealthy ?? false,
                LastHeartbeat = DateTimeOffset.UtcNow,
                VersionKey = heartbeat.ServiceVersion ?? string.Empty,
                ServiceVersion = string.IsNullOrEmpty(heartbeat.ServiceVersion) ? null : heartbeat.ServiceVersion,
                RegistrationHash = heartbeat.RegistrationHash
            };
        }
    }

    /// <summary>Ingests a trace batch: the bounded ring window plus cumulative stats (which
    /// deliberately outlive the window). Returns how many events were accepted.</summary>
    public int AddEvents(IReadOnlyList<MeshTraceEvent> events)
    {
        // A wire payload can deserialize "events": null into an actual null - coalesce the same way
        // the per-event Status/TopicVersion fields already are below (spec §6: no missing feed ever
        // fails ingestion).
        events ??= Array.Empty<MeshTraceEvent>();
        lock (_lock)
        {
            var accepted = 0;
            foreach (var traceEvent in events)
            {
                // MeshTraceEvent is a reference type, so a wire payload can legally deserialize
                // "events": [null, {...}] - a non-null list that itself contains a null element
                // (#253, one level down from #234's whole-null-list fix). Skip it rather than
                // dereferencing it: the same §6 "no missing feed ever fails ingestion" rule this
                // file already applies to a null whole-list, a null status, and a null topic
                // version applies here too - one malformed element must not partially corrupt the
                // batch and fail every event after it.
                if (traceEvent == null)
                {
                    continue;
                }

                if (_ring.Count < _capacity)
                {
                    _ring.Add(traceEvent);
                }
                else
                {
                    var evicted = _ring[_next];
                    if (!string.IsNullOrEmpty(evicted.SpanId))
                    {
                        _spanService.Remove(evicted.SpanId);
                    }
                    _ring[_next] = traceEvent;
                    _next = (_next + 1) % _capacity;
                }

                if (!string.IsNullOrEmpty(traceEvent.SpanId) && !string.IsNullOrEmpty(traceEvent.Service))
                {
                    _spanService[traceEvent.SpanId] = traceEvent.Service!;
                }

                RecordObservedActivityAndDrift(traceEvent);

                var failed = !BenzeneResultStatusExtensions.IsSuccess(traceEvent.Status);

                // A wire payload can carry a null status; coalesce it (like TopicVersion above) so it
                // never reaches the Dictionary key path as null (ArgumentNullException would abort the
                // whole batch mid-loop, against the §6 "no feed fails ingestion" rule).
                var status = traceEvent.Status ?? string.Empty;
                var topic = EnsureTopic((traceEvent.Topic, traceEvent.TopicVersion ?? string.Empty));
                topic.Invocations++;
                topic.StatusCounts[status] = topic.StatusCounts.GetValueOrDefault(status) + 1;
                topic.TotalDurationMs += traceEvent.DurationMs;
                topic.LastSeen = DateTimeOffset.UtcNow;
                if (failed)
                {
                    topic.Errors++;
                }

                if (!string.IsNullOrEmpty(traceEvent.Service))
                {
                    var service = EnsureService(traceEvent.Service!);
                    service.Invocations++;
                    service.LastSeen = DateTimeOffset.UtcNow;
                    if (failed)
                    {
                        service.Errors++;
                    }
                }

                accepted++;
            }
            return accepted;
        }
    }

    /// <summary>Ingests an issue batch (spec §4.1): fingerprint-keyed delta merge (<c>count += delta</c>,
    /// <c>firstSeen = min</c>, <c>lastSeen = max</c>, exemplars keep the newest ≤3, error detail merged by
    /// (field, code) and bounded to <see cref="MeshIssueErrors.MaxEntries"/>, other fields latest-wins), bounded (evict oldest <c>lastSeen</c> when full). Invalid entries (no fingerprint
    /// or topic) are skipped, never rejected; an empty batch is the feed's liveness assertion and marks
    /// the service's issue feed as wired. Returns how many entries were accepted.</summary>
    public int AddIssues(MeshIssueBatch batch)
    {
        // A wire payload can deserialize "issues": null into an actual null - coalesce so an empty/
        // missing list is accepted as a no-op batch (still marking the feed as wired below) rather
        // than throwing (spec §6: no missing feed ever fails ingestion).
        batch.Issues ??= new List<MeshIssue>();

        lock (_lock)
        {
            EnsureService(batch.Service).IssueFeedSeen = true;

            var accepted = 0;
            foreach (var incoming in batch.Issues)
            {
                // MeshIssue is a reference type, so a wire payload can legally deserialize
                // "issues": [null, {...}] - a non-null list that itself contains a null element
                // (#253, the same exposure AddEvents had for MeshTraceEvent). Skip it rather than
                // dereferencing it - never rejected, matching the missing-fingerprint/topic skip
                // just below.
                if (incoming == null)
                {
                    continue;
                }

                // Same tolerance one level down: an individual issue's exemplar list can itself
                // deserialize to null.
                incoming.ExemplarTraceIds ??= new List<string>();
                if (string.IsNullOrEmpty(incoming.Fingerprint) || string.IsNullOrEmpty(incoming.Topic))
                {
                    continue; // skipped, never rejected (§6: no feed fails ingestion)
                }

                if (!_issues.TryGetValue(incoming.Fingerprint, out var issue))
                {
                    if (_issues.Count >= _maxIssues)
                    {
                        // Evict the least recently observed issue — the least actionable one.
                        var oldest = _issues.Values.OrderBy(x => x.LastSeen).First();
                        _issues.Remove(oldest.Fingerprint);
                    }
                    issue = new MeshIssue
                    {
                        Fingerprint = incoming.Fingerprint,
                        Classification = incoming.Classification,
                        Service = incoming.Service,
                        Topic = incoming.Topic,
                        Version = incoming.Version,
                        FirstSeen = incoming.FirstSeen,
                        LastSeen = incoming.LastSeen
                    };
                    _issues[incoming.Fingerprint] = issue;
                }

                issue.Count += incoming.Count; // deltas merge by summation — restart-proof, no instance keying
                if (incoming.FirstSeen < issue.FirstSeen) issue.FirstSeen = incoming.FirstSeen;
                if (incoming.LastSeen > issue.LastSeen) issue.LastSeen = incoming.LastSeen;
                issue.Classification = string.IsNullOrEmpty(incoming.Classification) ? issue.Classification : incoming.Classification;
                issue.Transport = incoming.Transport ?? issue.Transport;
                issue.Status = string.IsNullOrEmpty(incoming.Status) ? issue.Status : incoming.Status;
                issue.ExceptionType = incoming.ExceptionType ?? issue.ExceptionType;
                issue.ResolutionHint = incoming.ResolutionHint ?? issue.ResolutionHint;
                foreach (var exemplar in incoming.ExemplarTraceIds)
                {
                    if (string.IsNullOrEmpty(exemplar) || issue.ExemplarTraceIds.Contains(exemplar))
                    {
                        continue;
                    }
                    issue.ExemplarTraceIds.Add(exemplar);
                    if (issue.ExemplarTraceIds.Count > 3)
                    {
                        issue.ExemplarTraceIds.RemoveAt(0); // keep the newest
                    }
                }
                // Error detail (spec §4.1): merge by (field, code) identity, count += delta, then the same
                // bound and order the emitter applies. Malformed wire entries were already skipped by the
                // lenient decoder; Merge skips any an in-process caller hands in.
                issue.Errors = MeshIssueErrors.Merge(issue.Errors, incoming.Errors);
                accepted++;
            }
            return accepted;
        }
    }

    public FleetView Fleet(MeshTimeRange? range = null)
    {
        lock (_lock)
        {
            var window = MeshTimeRangeResolver.Resolve(range, DateTimeOffset.UtcNow);
            return new FleetView
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Services = _services.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(ServiceSummaryLocked).ToList(),
                Topics = _topics.Keys
                    .OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal)
                    .Select(TopicSummaryLocked)
                    .ToList(),
                // Flows honor the window (ring filtered by trace start); the per-topic/service counts above
                // are cumulative-since-start and can't be sub-windowed - CollectorWindow says so.
                Traces = TraceSummariesLocked(MaxFleetTraces, window),
                // The merged issue map, newest activity first. NOT window-filtered (a merged map, like the
                // cumulative counts) - readers window on lastSeen client-side. Snapshot copies so later
                // ingest merges can't tear a view being serialized outside this lock.
                Issues = _issues.Values
                    .OrderByDescending(x => x.LastSeen)
                    .Select(x => new MeshIssue
                    {
                        Fingerprint = x.Fingerprint,
                        Classification = x.Classification,
                        Service = x.Service,
                        Topic = x.Topic,
                        Version = x.Version,
                        Transport = x.Transport,
                        Status = x.Status,
                        ExceptionType = x.ExceptionType,
                        Count = x.Count,
                        FirstSeen = x.FirstSeen,
                        LastSeen = x.LastSeen,
                        ExemplarTraceIds = x.ExemplarTraceIds.ToList(),
                        ResolutionHint = x.ResolutionHint,
                        Errors = x.Errors?
                            .Select(e => new MeshIssueError { Field = e.Field, Code = e.Code, Count = e.Count })
                            .ToList()
                    })
                    .ToList(),
                Window = CollectorWindow(window)
            };
        }
    }

    public ServiceView? Service(string name, MeshTimeRange? range = null)
    {
        lock (_lock)
        {
            if (!_services.TryGetValue(name, out var state))
            {
                return null;
            }

            var summary = ServiceSummaryLocked(name);
            var view = new ServiceView
            {
                Service = summary.Service,
                Runtime = summary.Runtime,
                Binding = summary.Binding,
                Placement = summary.Placement,
                Topics = summary.Topics,
                ServiceVersion = summary.ServiceVersion,
                Health = summary.Health,
                LastSeen = summary.LastSeen,
                Invocations = summary.Invocations,
                Errors = summary.Errors,
                MissingFeeds = summary.MissingFeeds,
                Registration = state.Registration,
                Instances = state.Instances
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(pair => new InstanceView
                    {
                        InstanceId = pair.Key,
                        ServiceVersion = pair.Value.ServiceVersion,
                        Healthy = pair.Value.Healthy,
                        LastHeartbeat = pair.Value.LastHeartbeat,
                        RegistrationHash = pair.Value.RegistrationHash,
                        HashMatches = HashMatches(state, pair.Value)
                    })
                    .ToList(),
                // D26: the per-version breakdown, oldest registration first (the headline is last).
                Versions = state.Registrations
                    .OrderBy(x => state.RegisteredAt.GetValueOrDefault(x.Key, 0))
                    .Select(x => new ServiceVersionSummary
                    {
                        ServiceVersion = x.Value.ServiceVersion,
                        Topics = ConsumedKeys(x.Value).Count()
                    })
                    .ToList(),
                // The service's counts are cumulative-since-start; a requested window is reported (with
                // CountsWindowed=false) so the page can badge them honestly. The service's live flows are
                // windowed on the fleet poll, not carried here.
                Window = CollectorWindow(MeshTimeRangeResolver.Resolve(range, DateTimeOffset.UtcNow))
            };
            return view;
        }
    }

    public TopicSummary? Topic(string id, string? version, MeshTimeRange? range = null)
    {
        lock (_lock)
        {
            var key = (id, version ?? string.Empty);
            if (!_topics.ContainsKey(key))
            {
                return null;
            }
            var summary = TopicSummaryLocked(key);
            // Standalone topic response carries the window (cumulative counts on this plane); embedded in a
            // FleetView it stays null - the fleet's one Window covers the whole view.
            summary.Window = CollectorWindow(MeshTimeRangeResolver.Resolve(range, DateTimeOffset.UtcNow));
            return summary;
        }
    }

    public TraceView? Trace(string traceId)
    {
        lock (_lock)
        {
            var events = _ring.Where(x => x.TraceId == traceId).OrderBy(x => x.StartedAt).ToList();
            return events.Count == 0 ? null : new TraceView { TraceId = traceId, Events = events };
        }
    }

    /// <summary>
    /// Every flow in the ring that carried <paramref name="correlationId"/>, grouped by trace id -
    /// one <see cref="TraceView"/> per trace (events in start order), traces ordered by earliest
    /// event. A correlation id is a business identifier that can span multiple traces, so the result
    /// preserves that grouping rather than flattening. Events with a null correlation id never match
    /// (the mesh never fabricates a correlation id). Returns null when nothing carried it.
    /// </summary>
    public CorrelationView? Correlation(string correlationId, MeshTimeRange? range = null)
    {
        lock (_lock)
        {
            var window = MeshTimeRangeResolver.Resolve(range, DateTimeOffset.UtcNow);
            var traces = _ring
                .Where(x => x.CorrelationId == correlationId)
                .GroupBy(x => x.TraceId)
                .Select(group => new TraceView
                {
                    TraceId = group.Key,
                    Events = group.OrderBy(x => x.StartedAt).ToList(),
                })
                // A flow is in-window when it started in [From,To] - the same trace-start rule the fleet
                // recent-flows list uses, so a window filters both consistently.
                .Where(view => window == null ||
                    (view.Events[0].StartedAt >= window.Value.From && view.Events[0].StartedAt <= window.Value.To))
                .OrderBy(view => view.Events[0].StartedAt)
                .ToList();
            return traces.Count == 0
                ? null
                : new CorrelationView { CorrelationId = correlationId, Traces = traces, Window = CollectorWindow(window) };
        }
    }

    /// <summary>Build the reported <see cref="MeshWindow"/> for this (push-collector) plane: flows honor
    /// <paramref name="window"/>, but counts are cumulative since <see cref="StartedAtUtc"/> - so
    /// <see cref="MeshWindow.CountsWindowed"/> is false and <see cref="MeshWindow.CountsSince"/> names when the
    /// counts really cover from. Null when no window was requested (the field is then omitted - today's shape).</summary>
    private MeshWindow? CollectorWindow((DateTimeOffset From, DateTimeOffset To)? window)
        => window == null
            ? null
            : new MeshWindow
            {
                From = MeshTimeRangeResolver.ToIso(window.Value.From),
                To = MeshTimeRangeResolver.ToIso(window.Value.To),
                CountsWindowed = false,
                CountsSince = MeshTimeRangeResolver.ToIso(StartedAtUtc)
            };

    // IMeshFleetReadModel — the in-memory store is synchronous, so these just wrap the read methods
    // above (the query handlers depend on the async interface so a backend-composed reader can slot in).
    Task<FleetView> IMeshFleetReadModel.FleetAsync(MeshTimeRange? range, CancellationToken cancellationToken) => Task.FromResult(Fleet(range));
    Task<ServiceView?> IMeshFleetReadModel.ServiceAsync(string name, MeshTimeRange? range, CancellationToken cancellationToken) => Task.FromResult(Service(name, range));
    Task<TopicSummary?> IMeshFleetReadModel.TopicAsync(string id, string? version, MeshTimeRange? range, CancellationToken cancellationToken) => Task.FromResult(Topic(id, version, range));
    Task<TraceView?> IMeshFleetReadModel.TraceAsync(string traceId, CancellationToken cancellationToken) => Task.FromResult(Trace(traceId));
    Task<CorrelationView?> IMeshFleetReadModel.CorrelationAsync(string correlationId, MeshTimeRange? range, CancellationToken cancellationToken) => Task.FromResult(Correlation(correlationId, range));

    private ServiceState EnsureService(string name)
    {
        if (!_services.TryGetValue(name, out var state))
        {
            state = new ServiceState();
            _services[name] = state;
        }
        return state;
    }

    /// <summary>Whether an instance's reported <c>registrationHash</c> matches the document registered
    /// for ITS OWN <c>(service, serviceVersion)</c> (mesh.md §5) - the heartbeat's
    /// <c>serviceVersion</c> picks the registration, and its hash is compared with that registration's
    /// carried <c>registrationHash</c>, never with a different release's: two releases deployed side by
    /// side legitimately report different hashes. Null (unknown, never drift) when the instance reported
    /// no hash, when no document is registered for its version (an unregistered instance, not a
    /// mismatch), or when that document carried no hash.</summary>
    private static bool? HashMatches(ServiceState state, InstanceState instance)
    {
        if (instance.RegistrationHash == null
            || !state.Registrations.TryGetValue(instance.VersionKey, out var registration)
            || registration.RegistrationHash == null)
        {
            return null;
        }

        return registration.RegistrationHash == instance.RegistrationHash;
    }

    private TopicState EnsureTopic((string Id, string Version) key)
    {
        if (!_topics.TryGetValue(key, out var state))
        {
            state = new TopicState();
            _topics[key] = state;
        }
        return state;
    }

    /// <summary>
    /// §4.2's two observed-only signals, both derived from this one trace event and both layered on
    /// top of the declared graph (§4) - neither ever adds, removes, or otherwise touches
    /// <see cref="TopicState.Providers"/>/<see cref="TopicState.Consumers"/>:
    /// <list type="bullet">
    /// <item><b>Activity</b> (feeds "Unobserved"): records that this topic/version was actually
    /// exercised - as CONSUMER by the handling service (<see cref="MeshTraceEvent.Service"/>), and as
    /// PROVIDER by the calling service, found via <see cref="_spanService"/> off the parent span - so
    /// <see cref="TopicSummaryLocked"/> can report a per-edge last-observed-at rather than a boolean.
    /// The two sides follow the same 2026-08 inversion as the declared graph: whoever handles the
    /// message consumes it, whoever sent it provides it.</item>
    /// <item><b>Undeclared → contract-drift</b>: when the handling/calling service HAS registered, with
    /// the relevant members known, but this topic isn't in its declared <c>requests</c> (handler) or
    /// <c>events</c> ∪ <c>sends</c> (caller), the
    /// running system disagrees with the declared contract - filed as a contract-drift issue (spec
    /// §4.1's classification vocabulary already reserves this bucket) via the same merged <see cref="_issues"/>
    /// map the emitter-reported feed uses. An anonymous/never-registered service is never flagged - it
    /// has no contract to diverge from - nor is one whose relevant member is unknown.</item>
    /// </list>
    /// </summary>
    private void RecordObservedActivityAndDrift(MeshTraceEvent traceEvent)
    {
        if (string.IsNullOrEmpty(traceEvent.Topic))
        {
            return;
        }

        var key = (traceEvent.Topic, traceEvent.TopicVersion ?? string.Empty);
        var observedAt = DateTimeOffset.UtcNow;
        // A reserved (benzene:) topic is never an undeclared edge (mesh.md §4.2): no document lists one
        // (contract-document.md §1), so a trace of benzene:spec or benzene:healthcheck is no drift.
        var mayDrift = !Benzene.Abstractions.BenzeneTopic.IsReserved(traceEvent.Topic);

        if (!string.IsNullOrEmpty(traceEvent.Service))
        {
            var handler = traceEvent.Service!;
            EnsureActivity(_consumerActivity, key)[handler] = observedAt;

            if (mayDrift && _services.TryGetValue(handler, out var handlerState) &&
                CanBeUndeclared(handlerState, consumerSide: true) &&
                !ContainsTopicInAnyLiveVersion(handlerState, key, consumerSide: true))
            {
                FileContractDrift(handler, traceEvent);
            }
        }

        if (!string.IsNullOrEmpty(traceEvent.ParentSpanId) &&
            _spanService.TryGetValue(traceEvent.ParentSpanId!, out var caller) &&
            caller != traceEvent.Service)
        {
            EnsureActivity(_providerActivity, key)[caller] = observedAt;

            if (mayDrift && _services.TryGetValue(caller, out var callerState) &&
                CanBeUndeclared(callerState, consumerSide: false) &&
                !ContainsTopicInAnyLiveVersion(callerState, key, consumerSide: false))
            {
                FileContractDrift(caller, traceEvent);
            }
        }
    }

    /// <summary>The recorded last-observed instant for <paramref name="service"/> on this edge, or
    /// null when it has never been observed - never the struct default (spec §4.2: absence, not a
    /// collapsed epoch/boolean).</summary>
    private static DateTimeOffset? LastObservedAt(Dictionary<string, DateTimeOffset>? activity, string service)
        => activity != null && activity.TryGetValue(service, out var when) ? when : null;

    private static Dictionary<string, DateTimeOffset> EnsureActivity(
        Dictionary<(string Id, string Version), Dictionary<string, DateTimeOffset>> activity, (string Id, string Version) key)
    {
        if (!activity.TryGetValue(key, out var map))
        {
            map = new Dictionary<string, DateTimeOffset>();
            activity[key] = map;
        }
        return map;
    }

    /// <summary>A service "has a contract to diverge from" on one side only when it has registered and
    /// EVERY currently live registration knows that side's members - <c>requests</c> for the consumer
    /// side, both <c>events</c> and <c>sends</c> for the provider side. A member that is absent or named
    /// in <c>degraded</c> is unknown (mesh.md §2): the topic might be in it, so a trace on it is not
    /// undeclared (§4.2), and flagging it would be a false positive. Checked across every live version
    /// (#283), like <see cref="ContainsTopicInAnyLiveVersion"/>.</summary>
    private static bool CanBeUndeclared(ServiceState state, bool consumerSide)
        => state.Registrations.Count > 0 && state.Registrations.Values.All(r => consumerSide
            ? r.IsKnown(MeshRegistrationMembers.Requests)
            : r.IsKnown(MeshRegistrationMembers.Events) && r.IsKnown(MeshRegistrationMembers.Sends));

    /// <summary>Whether ANY currently live version of the service declares <paramref name="key"/> on
    /// the relevant side (consumer: <c>requests</c>; provider: <c>events</c> ∪ <c>sends</c>) - the #283
    /// fix: a trace event from an OLDER, still-live, correctly-registered version must not be checked
    /// against a NEWER sibling's declared topics only, or a healthy side-by-side canary/blue-green
    /// deployment is misfiled as contract drift. Accepted trade-off, documented rather than a silent
    /// false positive: <c>MeshTraceEvent</c> carries no per-event <c>serviceVersion</c> (tracked in
    /// <c>work/outstanding-bugs.md</c>), so a genuine single-version drift on an edge another live
    /// version also declares goes undetected until that other version retires.</summary>
    private static bool ContainsTopicInAnyLiveVersion(ServiceState state, (string Id, string Version) key, bool consumerSide)
        => state.Registrations.Values.Any(r => (consumerSide ? ConsumedKeys(r) : ProvidedKeys(r)).Contains(key));

    /// <summary>Synthesizes one contract-drift occurrence and merges it into the same fingerprint-keyed
    /// <see cref="_issues"/> map <see cref="AddIssues"/> merges emitter-reported occurrences into (spec
    /// §4.1's merge rule: <c>count += 1</c> per observed occurrence here, <c>firstSeen</c>/<c>lastSeen</c>
    /// span the observations, newest ≤3 exemplar trace ids). The fingerprint's discriminator is the
    /// fixed <see cref="UndeclaredEdgeDiscriminator"/>, not the exercising call's status/exception - the
    /// edge's identity shouldn't fragment across whatever outcome happened to trigger detection.</summary>
    private void FileContractDrift(string service, MeshTraceEvent traceEvent)
    {
        var version = string.IsNullOrEmpty(traceEvent.TopicVersion) ? null : traceEvent.TopicVersion;
        var fingerprint = MeshIssueFingerprint.Compute(
            service, traceEvent.Topic, version, MeshIssueClassification.ContractDrift, null, UndeclaredEdgeDiscriminator);

        if (!_issues.TryGetValue(fingerprint, out var issue))
        {
            if (_issues.Count >= _maxIssues)
            {
                var oldest = _issues.Values.OrderBy(x => x.LastSeen).First();
                _issues.Remove(oldest.Fingerprint);
            }
            issue = new MeshIssue
            {
                Fingerprint = fingerprint,
                Classification = MeshIssueClassification.ContractDrift,
                Service = service,
                Topic = traceEvent.Topic,
                Version = version,
                FirstSeen = traceEvent.StartedAt,
                LastSeen = traceEvent.StartedAt
            };
            _issues[fingerprint] = issue;
        }

        issue.Count += 1;
        if (traceEvent.StartedAt < issue.FirstSeen) issue.FirstSeen = traceEvent.StartedAt;
        if (traceEvent.StartedAt > issue.LastSeen) issue.LastSeen = traceEvent.StartedAt;
        if (!string.IsNullOrEmpty(traceEvent.TraceId) && !issue.ExemplarTraceIds.Contains(traceEvent.TraceId))
        {
            issue.ExemplarTraceIds.Add(traceEvent.TraceId);
            if (issue.ExemplarTraceIds.Count > 3)
            {
                issue.ExemplarTraceIds.RemoveAt(0); // keep the newest
            }
        }
    }

    private ServiceSummary ServiceSummaryLocked(string name)
    {
        var state = _services[name];
        var summary = new ServiceSummary
        {
            Service = name,
            Health = MeshHealth.Unknown,
            LastSeen = state.LastSeen,
            Instances = state.Instances.Count,
            Invocations = state.Invocations,
            Errors = state.Errors
        };

        var registration = state.Registration;
        if (registration != null)
        {
            summary.Runtime = registration.Runtime;
            // The read model's binding (Provisional): the registration's first transport, now that the
            // document carries transports rather than a single binding.
            summary.Binding = registration.Transports?.FirstOrDefault(x => !string.IsNullOrEmpty(x));
            summary.Placement = registration.Placement ?? new MeshPlacement();
            summary.Topics = ConsumedKeys(registration).Count();
            summary.ServiceVersion = registration.ServiceVersion;
        }
        else
        {
            // Known only from traffic: anonymous but live. The read models name the registration feed
            // "descriptor" (mesh.md §6).
            summary.MissingFeeds.Add("descriptor");
        }

        if (state.Instances.Count == 0)
        {
            summary.MissingFeeds.Add("health");
        }
        else
        {
            summary.Health = state.Instances.Values.All(x => x.Healthy) ? MeshHealth.Healthy : MeshHealth.Degraded;
        }

        if (state.Invocations == 0)
        {
            summary.MissingFeeds.Add("traces");
        }
        // Feed-absence only matters when there's failure it should have explained: a service with
        // failing traffic that has never sent a mesh:issues batch (not even the empty liveness one)
        // is flagged; a healthy never-emitting service is indistinguishable from a healthy emitting
        // one, and that's fine (spec §4.1 / drains-up 3.2 ruling).
        if (!state.IssueFeedSeen && state.Errors > 0)
        {
            summary.MissingFeeds.Add("issues");
        }
        return summary;
    }

    private TopicSummary TopicSummaryLocked((string Id, string Version) key)
    {
        var state = _topics[key];
        var providerActivity = _providerActivity.GetValueOrDefault(key);
        var consumerActivity = _consumerActivity.GetValueOrDefault(key);
        return new TopicSummary
        {
            Topic = key.Id,
            Version = string.IsNullOrEmpty(key.Version) ? null : key.Version,
            // Declared, unconditionally (spec §4, 2026-08 revision) - the full graph even for a
            // service that registered but never sent or received a single message.
            Providers = state.Providers.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Consumers = state.Consumers.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            // Observed-only, additive (spec §4.2): per declared edge, when it was last actually
            // exercised - absent (not false) when never observed, so a reader can judge staleness
            // rather than read a collapsed boolean.
            ProviderActivity = state.Providers.OrderBy(x => x, StringComparer.Ordinal)
                .ToDictionary(x => x, x => new MeshEdgeActivity { LastObservedAt = LastObservedAt(providerActivity, x) }),
            ConsumerActivity = state.Consumers.OrderBy(x => x, StringComparer.Ordinal)
                .ToDictionary(x => x, x => new MeshEdgeActivity { LastObservedAt = LastObservedAt(consumerActivity, x) }),
            Invocations = state.Invocations,
            Errors = state.Errors,
            AvgDurationMs = state.Invocations > 0 ? state.TotalDurationMs / state.Invocations : 0,
            StatusCounts = state.StatusCounts.ToDictionary(x => x.Key, x => x.Value),
            LastSeen = state.LastSeen
        };
    }

    private List<TraceSummary> TraceSummariesLocked(int limit, (DateTimeOffset From, DateTimeOffset To)? window = null)
    {
        return _ring
            .GroupBy(x => x.TraceId)
            .Select(group =>
            {
                var startedAt = group.Min(x => x.StartedAt);
                var end = group.Max(x => x.StartedAt + TimeSpan.FromMilliseconds(x.DurationMs));
                var failed = group.Any(x => !BenzeneResultStatusExtensions.IsSuccess(x.Status));
                return new TraceSummary
                {
                    TraceId = group.Key,
                    Events = group.Count(),
                    Services = group.Where(x => !string.IsNullOrEmpty(x.Service))
                        .Select(x => x.Service!).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    StartedAt = startedAt,
                    DurationMs = (end - startedAt).TotalMilliseconds,
                    Failed = failed,
                    Status = MeshFlowStatus.Of(group, failed),
                    // The flow's entry topic: the earliest event's. Ring events always carry a topic.
                    Topic = group.OrderBy(x => x.StartedAt)
                        .Select(x => x.Topic).FirstOrDefault(t => !string.IsNullOrEmpty(t))
                };
            })
            // A flow is in-window when it started in [From,To]; no window ⇒ today's unfiltered last-N.
            .Where(t => window == null || (t.StartedAt >= window.Value.From && t.StartedAt <= window.Value.To))
            .OrderByDescending(x => x.StartedAt)
            .Take(limit)
            .ToList();
    }
}

/// <summary>The wire-contracts §3 success class, applied to a trace event's status: an unknown or
/// empty status counts as a failure, matching every per-protocol mapping table's default.</summary>
public static class BenzeneResultStatusExtensions
{
    private static readonly HashSet<string> SuccessStatuses = new()
    {
        BenzeneResultStatus.Ok,
        BenzeneResultStatus.Created,
        BenzeneResultStatus.Accepted,
        BenzeneResultStatus.Updated,
        BenzeneResultStatus.Deleted,
        BenzeneResultStatus.Ignored
    };

    public static bool IsSuccess(string? status)
    {
        return status != null && SuccessStatuses.Contains(status);
    }
}

/// <summary>A flow's <c>status</c> on the fleet view (spec §4): how it ended for its caller.</summary>
public static class MeshFlowStatus
{
    /// <summary>The status of the earliest-started failing event when the flow <paramref name="failed"/>
    /// (an event fails exactly when it makes the summary failed: outside the wire-contracts §3 success
    /// class), otherwise of the earliest-started event. Equal start times keep the order of
    /// <paramref name="events"/> (receipt order; the sort is stable). Null when unknown — no events, an
    /// empty status, or a flow flagged failed with no failing event to name (a plane whose failure flag
    /// comes from somewhere other than the events' Benzene status) — so the wire omits it.</summary>
    public static string? Of(IEnumerable<MeshTraceEvent> events, bool failed)
    {
        var ordered = events.OrderBy(x => x.StartedAt);
        var status = failed
            ? ordered.FirstOrDefault(x => !BenzeneResultStatusExtensions.IsSuccess(x.Status))?.Status
            : ordered.FirstOrDefault()?.Status;
        return string.IsNullOrEmpty(status) ? null : status;
    }
}
