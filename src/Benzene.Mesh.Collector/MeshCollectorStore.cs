using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Wire;
using Benzene.Results;

namespace Benzene.Mesh.Collector;

/// <summary>
/// The in-memory state behind the spec collector (docs/specification/mesh.md §4-§6):
/// cumulative per-service and per-topic stats, the latest heartbeat per instance, registered
/// descriptors (the SOLE source of the producer/consumer graph, §4), and a bounded ring of recent
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

    public MeshCollectorStore(int maxTraceEvents = 4096, int maxIssues = 1024)
    {
        _capacity = maxTraceEvents;
        _maxIssues = maxIssues;
        _ring = new List<MeshTraceEvent>(Math.Min(maxTraceEvents, 1024));
    }

    /// <summary>
    /// When this store started accumulating - the window start for anything reporting the
    /// cumulative stats (storage is in-memory, so counts always cover "since process start").
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    private class ServiceState
    {
        public MeshServiceDescriptor? Descriptor;
        public readonly Dictionary<string, InstanceState> Instances = new();
        public DateTimeOffset LastSeen;
        public long Invocations;
        public long Errors;
        // True once ANY mesh:issues batch (including an empty liveness batch) named this service —
        // what lets "quiet wired feed" be distinguished from "feed not wired" (spec §4.1).
        public bool IssueFeedSeen;
    }

    private class InstanceState
    {
        public bool Healthy;
        public DateTimeOffset LastHeartbeat;
        public string? DescriptorHash;
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

    /// <summary>Stores the descriptor as the service's current contract, replacing any previous
    /// registration wholesale - a redeploy that drops a topic drops the provider claim with it, and
    /// one that drops a consumed topic drops the consumer claim with it, symmetrically (spec §4).
    /// This - <c>topics</c> for providers, <c>consumes</c> for consumers - is now the SOLE source of
    /// the producer/consumer graph (2026-08 revision): trace parentage never admits or removes an
    /// edge here, only feeds the separate observed signals in <see cref="RecordObservedActivityAndDrift"/>.</summary>
    public void Register(MeshServiceDescriptor descriptor)
    {
        lock (_lock)
        {
            foreach (var topic in _topics.Values)
            {
                topic.Providers.Remove(descriptor.Service);
                topic.Consumers.Remove(descriptor.Service);
            }

            var state = EnsureService(descriptor.Service);
            state.Descriptor = descriptor;
            state.LastSeen = DateTimeOffset.UtcNow;

            foreach (var topic in descriptor.Topics)
            {
                EnsureTopic((topic.Id, topic.Version ?? string.Empty)).Providers.Add(descriptor.Service);
            }
            foreach (var topic in descriptor.Consumes)
            {
                EnsureTopic((topic.Id, topic.Version ?? string.Empty)).Consumers.Add(descriptor.Service);
            }
        }
    }

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
                DescriptorHash = heartbeat.DescriptorHash
            };
        }
    }

    /// <summary>Ingests a trace batch: the bounded ring window plus cumulative stats (which
    /// deliberately outlive the window). Returns how many events were accepted.</summary>
    public int AddEvents(IReadOnlyList<MeshTraceEvent> events)
    {
        lock (_lock)
        {
            foreach (var traceEvent in events)
            {
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
            }
            return events.Count;
        }
    }

    /// <summary>Ingests an issue batch (spec §4.1): fingerprint-keyed delta merge (<c>count += delta</c>,
    /// <c>firstSeen = min</c>, <c>lastSeen = max</c>, exemplars keep the newest ≤3, other fields
    /// latest-wins), bounded (evict oldest <c>lastSeen</c> when full). Invalid entries (no fingerprint
    /// or topic) are skipped, never rejected; an empty batch is the feed's liveness assertion and marks
    /// the service's issue feed as wired. Returns how many entries were accepted.</summary>
    public int AddIssues(MeshIssueBatch batch)
    {
        lock (_lock)
        {
            EnsureService(batch.Service).IssueFeedSeen = true;

            var accepted = 0;
            foreach (var incoming in batch.Issues)
            {
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
                        ResolutionHint = x.ResolutionHint
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
                Health = summary.Health,
                LastSeen = summary.LastSeen,
                Invocations = summary.Invocations,
                Errors = summary.Errors,
                MissingFeeds = summary.MissingFeeds,
                Descriptor = state.Descriptor,
                Instances = state.Instances
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(pair => new InstanceView
                    {
                        InstanceId = pair.Key,
                        Healthy = pair.Value.Healthy,
                        LastHeartbeat = pair.Value.LastHeartbeat,
                        DescriptorHash = pair.Value.DescriptorHash,
                        HashMatches = state.Descriptor?.DescriptorHash != null && pair.Value.DescriptorHash != null
                            ? pair.Value.DescriptorHash == state.Descriptor.DescriptorHash
                            : null
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
    /// exercised - as provider by the handling service (<see cref="MeshTraceEvent.Service"/>), and as
    /// consumer by the calling service, found via <see cref="_spanService"/> off the parent span - so
    /// <see cref="TopicSummaryLocked"/> can report a per-edge last-observed-at rather than a boolean.</item>
    /// <item><b>Undeclared → contract-drift</b>: when the handling/calling service HAS a registered,
    /// non-degraded descriptor, but this topic isn't in its declared <c>topics</c>/<c>consumes</c>, the
    /// running system disagrees with the declared contract - filed as a contract-drift issue (spec
    /// §4.1's classification vocabulary already reserves this bucket) via the same merged <see cref="_issues"/>
    /// map the emitter-reported feed uses. An anonymous/never-registered service (no descriptor at
    /// all) is never flagged - it has no contract to diverge from.</item>
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

        if (!string.IsNullOrEmpty(traceEvent.Service))
        {
            var handler = traceEvent.Service!;
            EnsureActivity(_providerActivity, key)[handler] = observedAt;

            if (_services.TryGetValue(handler, out var handlerState) &&
                IsDeclared(handlerState.Descriptor, MeshDescriptorFactory.RegistryFeed) &&
                !ContainsTopic(handlerState.Descriptor!.Topics, key))
            {
                FileContractDrift(handler, traceEvent);
            }
        }

        if (!string.IsNullOrEmpty(traceEvent.ParentSpanId) &&
            _spanService.TryGetValue(traceEvent.ParentSpanId!, out var caller) &&
            caller != traceEvent.Service)
        {
            EnsureActivity(_consumerActivity, key)[caller] = observedAt;

            if (_services.TryGetValue(caller, out var callerState) &&
                IsDeclared(callerState.Descriptor, MeshDescriptorFactory.OutboundRegistryFeed) &&
                !ContainsTopic(callerState.Descriptor!.Consumes, key))
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

    /// <summary>A service "has a contract to diverge from" only when it registered a descriptor AND
    /// that descriptor's relevant list isn't itself degraded (a port that hasn't wired up the
    /// registry/outbound-registry yet genuinely doesn't know its own topics/consumes, so flagging it
    /// would be a false positive, not a real drift).</summary>
    private static bool IsDeclared(MeshServiceDescriptor? descriptor, string feedName)
        => descriptor != null && !(descriptor.Degraded?.Contains(feedName) ?? false);

    private static bool ContainsTopic(List<MeshTopicDescriptor> topics, (string Id, string Version) key)
        => topics.Any(t => t.Id == key.Id && (t.Version ?? string.Empty) == key.Version);

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

        if (state.Descriptor != null)
        {
            summary.Runtime = state.Descriptor.Runtime;
            summary.Binding = state.Descriptor.Binding;
            summary.Placement = state.Descriptor.Placement;
            summary.Topics = state.Descriptor.Topics.Count;
        }
        else
        {
            summary.MissingFeeds.Add("descriptor"); // known only from traffic: anonymous but live
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
                return new TraceSummary
                {
                    TraceId = group.Key,
                    Events = group.Count(),
                    Services = group.Where(x => !string.IsNullOrEmpty(x.Service))
                        .Select(x => x.Service!).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    StartedAt = startedAt,
                    DurationMs = (end - startedAt).TotalMilliseconds,
                    Failed = group.Any(x => !BenzeneResultStatusExtensions.IsSuccess(x.Status)),
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
