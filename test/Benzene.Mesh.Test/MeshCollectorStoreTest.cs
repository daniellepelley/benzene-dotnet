using Benzene.Mesh.Collector;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Store behaviors the conformance sequences don't pin: the bounded ring window (eviction, with
/// cumulative stats deliberately outliving it) and the fleet flow-list cap.
/// </summary>
public class MeshCollectorStoreTest
{
    private static MeshTraceEvent Event(string traceId, string spanId, string service, string topic,
        DateTimeOffset startedAt, string status = "ok")
    {
        return new MeshTraceEvent
        {
            TraceId = traceId,
            SpanId = spanId,
            Service = service,
            Topic = topic,
            Status = status,
            DurationMs = 1,
            StartedAt = startedAt
        };
    }

    [Fact]
    public void AddEvents_EventWithNullStatus_IsAcceptedAndCountedAsFailure()
    {
        // A wire payload can deserialize "status": null into an actual null (nullable-reference
        // annotations are not enforced at runtime). The §6 degradation rule requires ingestion to
        // accept it rather than throw ArgumentNullException on the null status-count key.
        var store = new MeshCollectorStore();
        var evt = Event("trace-1", "span-1", "svc", "topic", DateTimeOffset.UtcNow, status: null!);

        var accepted = store.AddEvents(new[] { evt });

        Assert.Equal(1, accepted);
        var topic = store.Topic("topic", null);
        Assert.NotNull(topic);
        Assert.Equal(1, topic!.Invocations);
        Assert.Equal(1, topic.Errors);
    }

    [Fact]
    public void RingEviction_DropsTheWindowButKeepsCumulativeStats()
    {
        var store = new MeshCollectorStore(maxTraceEvents: 2);
        var now = DateTimeOffset.UtcNow;

        store.AddEvents(new[]
        {
            Event("trace-1", "span-1", "svc", "topic", now),
            Event("trace-1", "span-2", "svc", "topic", now.AddMilliseconds(1))
        });
        Assert.NotNull(store.Trace("trace-1"));

        store.AddEvents(new[]
        {
            Event("trace-2", "span-3", "svc", "topic", now.AddMilliseconds(2)),
            Event("trace-2", "span-4", "svc", "topic", now.AddMilliseconds(3))
        });

        Assert.Null(store.Trace("trace-1")); // aged out of the bounded window
        var topic = store.Topic("topic", null);
        Assert.NotNull(topic);
        Assert.Equal(4, topic!.Invocations); // cumulative stats outlive the ring
    }

    [Fact]
    public void FleetFlowList_IsCappedAtTwentyNewestFirst()
    {
        var store = new MeshCollectorStore();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 25; i++)
        {
            store.AddEvents(new[] { Event($"trace-{i}", $"span-{i}", "svc", "topic", now.AddSeconds(i)) });
        }

        var fleet = store.Fleet();

        Assert.Equal(20, fleet.Traces.Count);
        Assert.True(fleet.Traces[0].StartedAt > fleet.Traces[^1].StartedAt); // newest first
        Assert.Equal("topic", fleet.Traces[0].Topic); // the flow's entry topic (earliest event's)
    }

    private static List<MeshRegisteredEntry> Entries(string[]? topics) =>
        (topics ?? Array.Empty<string>()).Select(id => new MeshRegisteredEntry { Topic = id }).ToList();

    // A registration is the service's contract document (mesh.md §2); these carry only the members a
    // test needs, as the collector fixtures' bodies do.
    private static MeshRegistration Registration(
        string service, string[]? requests = null, string[]? sends = null, string? serviceVersion = null,
        string[]? events = null)
    {
        return new MeshRegistration
        {
            Service = service,
            Requests = Entries(requests),
            Events = Entries(events),
            Sends = Entries(sends),
            ServiceVersion = serviceVersion
        };
    }

    // ---- declared graph (spec §4): the SOLE source of producer/consumer edges ----
    //
    // Role assignment: `requests` (the topics a service HANDLES) makes it a CONSUMER of them; `events`
    // (what it publishes) and `sends` (what it sends to topics others define) make it a PROVIDER. That
    // is the way every broker in the field uses the words.

    [Fact]
    public void Graph_IsDeclaredFromRequestsAndSends_ZeroTrafficStillReportsTheFullGraph()
    {
        var store = new MeshCollectorStore();
        // payments HANDLES payments:capture, so it CONSUMES it. orders SENDS it, so it PROVIDES it.
        store.Register(Registration("payments", requests: new[] { "payments:capture" }));
        store.Register(Registration("orders", requests: new[] { "order:create" }, sends: new[] { "payments:capture" }));

        var topic = store.Topic("payments:capture", null);

        Assert.NotNull(topic);
        Assert.Equal(new List<string> { "orders" }, topic!.Providers);
        Assert.Equal(new List<string> { "payments" }, topic.Consumers);
        Assert.Equal(0, topic.Invocations); // declared, not a summary of traffic
    }

    [Fact]
    public void TraceParentage_NeverAdmitsOrRemovesAGraphEdge_OnlyFeedsStatsAndLiveness()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("greeter", requests: new[] { "greet" }));
        var now = DateTimeOffset.UtcNow;
        var caller = Event("trace-1", "span-parent", "frontdoor", "welcome", now);
        var callee = Event("trace-1", "span-child", "greeter", "greet", now.AddMilliseconds(1));
        callee.ParentSpanId = "span-parent";

        store.AddEvents(new[] { caller, callee });

        var greet = store.Topic("greet", null);
        Assert.NotNull(greet);
        // greeter HANDLES greet, so it is the consumer.
        Assert.Equal(new List<string> { "greeter" }, greet!.Consumers);
        // frontdoor called greeter but never declared "greet" in its produces - trace parentage
        // does NOT admit it as a provider (the central rule of the declared-graph revision).
        Assert.Empty(greet.Providers);
        Assert.Equal(1, greet.Invocations); // stats still come from the trace feed, unaffected
    }

    [Fact]
    public void Reregistration_ReplacesProviderAndConsumerEdges_Wholesale()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payments:capture" }));
        store.Register(Registration("orders", requests: new[] { "order:create" }, sends: new[] { "payments:capture" }));
        Assert.Equal(new List<string> { "orders" }, store.Topic("payments:capture", null)!.Providers);

        // orders redeploys and drops both the topic it handled and the one it produced.
        store.Register(Registration("orders", requests: new[] { "order:cancel" }));

        Assert.Empty(store.Topic("order:create", null)!.Consumers);
        Assert.Empty(store.Topic("payments:capture", null)!.Providers);
        Assert.Equal(new List<string> { "orders" }, store.Topic("order:cancel", null)!.Consumers);
    }

    // ---- ServiceVersion (mesh.md §2.5): retained on ingest, exposed on both query results ----

    [Fact]
    public void Register_WithServiceVersion_IsRetainedAndExposedOnFleetAndServiceQueries()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", requests: new[] { "order:create" }, serviceVersion: "2.3.1"));

        var summary = store.Fleet().Services.Single(s => s.Service == "orders");
        var view = store.Service("orders", null);

        Assert.Equal("2.3.1", summary.ServiceVersion);
        Assert.NotNull(view);
        Assert.Equal("2.3.1", view!.ServiceVersion);
        Assert.Equal("2.3.1", view.Registration?.ServiceVersion); // still on the registered document too
    }

    [Fact]
    public void Register_WithNoServiceVersion_LeavesItNull_NotDefaultedOrDropped()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", requests: new[] { "order:create" })); // no serviceVersion

        var summary = store.Fleet().Services.Single(s => s.Service == "orders");

        Assert.Null(summary.ServiceVersion);
    }

    [Fact]
    public void Reregistration_ReplacesServiceVersion_WithTheLatestRegistration()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", requests: new[] { "order:create" }, serviceVersion: "1.0.0"));
        store.Register(Registration("orders", requests: new[] { "order:create" }, serviceVersion: "2.0.0"));

        var summary = store.Fleet().Services.Single(s => s.Service == "orders");

        Assert.Equal("2.0.0", summary.ServiceVersion);
    }

    [Fact]
    public void ServiceView_ListsEveryLiveVersion_OldestRegistrationFirst()
    {
        // D26: one fleet row per service; the per-version breakdown lives on the service view.
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payment:take" }, serviceVersion: "1"));
        store.Register(Registration("payments", requests: new[] { "payment:take", "payment:refund" }, serviceVersion: "2"));

        var view = store.Service("payments", null)!;

        Assert.Single(store.Fleet().Services, s => s.Service == "payments");
        Assert.Equal(new[] { "1", "2" }, view.Versions.Select(v => v.ServiceVersion));
        Assert.Equal(new[] { 1, 2 }, view.Versions.Select(v => v.Topics));

        // Re-registering v1 makes it the most recent registration - and the headline.
        store.Register(Registration("payments", requests: new[] { "payment:take" }, serviceVersion: "1"));
        view = store.Service("payments", null)!;
        Assert.Equal(new[] { "2", "1" }, view.Versions.Select(v => v.ServiceVersion));
        Assert.Equal("1", view.ServiceVersion);
    }

    [Fact]
    public void ServiceView_Versions_AnUnversionedRegistrationIsOneEntryWithNoServiceVersion()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", requests: new[] { "order:create" }));

        var version = Assert.Single(store.Service("orders", null)!.Versions);
        Assert.Null(version.ServiceVersion);
        Assert.Equal(1, version.Topics);
    }

    // ---- §4.2 declared vs. observed: liveness ("Unobserved") ----

    [Fact]
    public void DeclaredEdge_WithNoMatchingTrace_ReportsAbsentLastObservedAt()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payments:capture" }));
        store.Register(Registration("orders", requests: new[] { "order:create" }, sends: new[] { "payments:capture" }));

        var topic = store.Topic("payments:capture", null)!;

        Assert.True(topic.ProviderActivity.ContainsKey("orders"));
        Assert.Null(topic.ProviderActivity["orders"].LastObservedAt);
        Assert.True(topic.ConsumerActivity.ContainsKey("payments"));
        Assert.Null(topic.ConsumerActivity["payments"].LastObservedAt);
    }

    [Fact]
    public void DeclaredEdge_OnceExercised_ReportsLastObservedAt()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payments:capture" }));
        store.Register(Registration("orders", requests: new[] { "order:create" }, sends: new[] { "payments:capture" }));
        var now = DateTimeOffset.UtcNow;
        var caller = Event("trace-1", "span-parent", "orders", "order:create", now);
        var callee = Event("trace-1", "span-child", "payments", "payments:capture", now.AddMilliseconds(1));
        callee.ParentSpanId = "span-parent";

        store.AddEvents(new[] { caller, callee });

        // The observed side follows the declared side: whoever SENT it provided it, whoever
        // HANDLED it consumed it.
        var topic = store.Topic("payments:capture", null)!;
        Assert.NotNull(topic.ProviderActivity["orders"].LastObservedAt);
        Assert.NotNull(topic.ConsumerActivity["payments"].LastObservedAt);
    }

    // ---- §4.2 declared vs. observed: drift ("Undeclared" → contract-drift) ----

    [Fact]
    public void UndeclaredConsumerEdge_FilesAContractDriftIssue()
    {
        var store = new MeshCollectorStore();
        // "greeter" registers but never declares "greet" among the topics it handles.
        store.Register(Registration("greeter", requests: Array.Empty<string>()));

        store.AddEvents(new[] { Event("trace-1", "span-1", "greeter", "greet", DateTimeOffset.UtcNow) });

        var issue = Assert.Single(store.Fleet().Issues);
        Assert.Equal(MeshIssueClassification.ContractDrift, issue.Classification);
        Assert.Equal("greeter", issue.Service);
        Assert.Equal("greet", issue.Topic);
    }

    [Fact]
    public void UndeclaredProviderEdge_FilesAContractDriftIssue()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payments:capture" }));
        // "orders" registers but never declares "payments:capture" in its produces.
        store.Register(Registration("orders", requests: new[] { "order:create" }));
        var now = DateTimeOffset.UtcNow;
        var caller = Event("trace-1", "span-parent", "orders", "order:create", now);
        var callee = Event("trace-1", "span-child", "payments", "payments:capture", now.AddMilliseconds(1));
        callee.ParentSpanId = "span-parent";

        store.AddEvents(new[] { caller, callee });

        var driftIssue = store.Fleet().Issues.Single(x => x.Service == "orders");
        Assert.Equal(MeshIssueClassification.ContractDrift, driftIssue.Classification);
        Assert.Equal("payments:capture", driftIssue.Topic);
    }

    [Fact]
    public void RepeatedUndeclaredCalls_MergeIntoOneIssue_CountingEachOccurrence()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("greeter", requests: Array.Empty<string>()));
        var now = DateTimeOffset.UtcNow;

        store.AddEvents(new[] { Event("trace-1", "span-1", "greeter", "greet", now) });
        store.AddEvents(new[] { Event("trace-2", "span-2", "greeter", "greet", now.AddSeconds(1)) });

        var issue = Assert.Single(store.Fleet().Issues);
        Assert.Equal(2, issue.Count);
    }

    [Fact]
    public void AnonymousNeverRegisteredService_IsNeverFlaggedAsDrift()
    {
        // A service the collector only knows from traffic has no contract to diverge from.
        var store = new MeshCollectorStore();

        store.AddEvents(new[] { Event("trace-1", "span-1", "frontdoor", "welcome", DateTimeOffset.UtcNow) });

        Assert.Empty(store.Fleet().Issues);
    }

    [Fact]
    public void DegradedRequests_AreUnknown_NeverFlaggedAsDrift()
    {
        // A service that HAS registered but named requests in degraded doesn't know its own topics -
        // the member is unknown (mesh.md §2), so a trace on it is not undeclared.
        var store = new MeshCollectorStore();
        store.Register(new MeshRegistration
        {
            Service = "greeter", Requests = new List<MeshRegisteredEntry>(), Degraded = new List<string> { "requests" }
        });

        store.AddEvents(new[] { Event("trace-1", "span-1", "greeter", "greet", DateTimeOffset.UtcNow) });

        Assert.Empty(store.Fleet().Issues);
    }

    [Fact]
    public void AbsentRequests_AreUnknown_NeverFlaggedAsDrift()
    {
        // A body carrying only service (mesh.md §2): every member is unknown.
        var store = new MeshCollectorStore();
        store.Register(new MeshRegistration { Service = "greeter" });

        store.AddEvents(new[] { Event("trace-1", "span-1", "greeter", "greet", DateTimeOffset.UtcNow) });

        Assert.Empty(store.Fleet().Issues);
        Assert.Equal(0, store.Service("greeter")!.Topics);
    }

    [Fact]
    public void UnknownSends_TheCallerIsNotFlaggedAsAnUndeclaredProvider()
    {
        // The caller's sends are degraded: the topic might be among them, so the edge is not undeclared.
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payments:capture" }));
        store.Register(new MeshRegistration
        {
            Service = "orders",
            Requests = Entries(new[] { "order:create" }),
            Events = new List<MeshRegisteredEntry>(),
            Sends = new List<MeshRegisteredEntry>(),
            Degraded = new List<string> { "sends" }
        });
        var now = DateTimeOffset.UtcNow;
        var caller = Event("trace-1", "span-parent", "orders", "order:create", now);
        var callee = Event("trace-1", "span-child", "payments", "payments:capture", now.AddMilliseconds(1));
        callee.ParentSpanId = "span-parent";

        store.AddEvents(new[] { caller, callee });

        Assert.DoesNotContain(store.Fleet().Issues, x => x.Service == "orders");
    }

    [Fact]
    public void APublishedEvent_MakesItsPublisherAProvider_AndIsNotUndeclared()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", requests: new[] { "order:create" }, events: new[] { "order:placed" }));
        store.Register(Registration("shipping", requests: new[] { "order:placed" }));
        var now = DateTimeOffset.UtcNow;
        var publisher = Event("trace-1", "span-parent", "orders", "order:create", now);
        var subscriber = Event("trace-1", "span-child", "shipping", "order:placed", now.AddMilliseconds(1));
        subscriber.ParentSpanId = "span-parent";

        store.AddEvents(new[] { publisher, subscriber });

        var topic = store.Topic("order:placed", null)!;
        Assert.Equal(new[] { "orders" }, topic.Providers);
        Assert.Equal(new[] { "shipping" }, topic.Consumers);
        Assert.Empty(store.Fleet().Issues);
    }

    [Fact]
    public void ATraceOfAReservedTopic_IsNeverAnUndeclaredEdge()
    {
        // mesh.md §4.2: no document lists a benzene: topic, so a trace of one is never contract-drift -
        // on either side of the edge.
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", requests: new[] { "order:create" }));
        store.Register(Registration("payments", requests: new[] { "payments:capture" }));
        var now = DateTimeOffset.UtcNow;
        var caller = Event("trace-1", "span-parent", "orders", "order:create", now);
        var spec = Event("trace-1", "span-child", "payments", "benzene:spec", now.AddMilliseconds(1));
        spec.ParentSpanId = "span-parent";

        store.AddEvents(new[]
        {
            caller, spec,
            Event("trace-2", "span-2", "orders", "benzene:healthcheck", now)
        });

        Assert.Empty(store.Fleet().Issues);
    }

    [Fact]
    public void BenzeneEntries_ContributeNoEdges()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", requests: new[] { "order:create", "benzene:spec" },
            sends: new[] { "benzene:mesh:traces" }));

        Assert.Null(store.Topic("benzene:spec", null));
        Assert.Null(store.Topic("benzene:mesh:traces", null));
        Assert.Equal(1, store.Service("orders")!.Topics);
    }

    // ---- mesh.md §5: a heartbeat's registrationHash is compared with ITS OWN version's registration ----

    [Fact]
    public void HeartbeatHash_IsComparedWithTheRegistrationForItsOwnServiceVersion()
    {
        var store = new MeshCollectorStore();
        store.Register(new MeshRegistration { Service = "payments", ServiceVersion = "1", RegistrationHash = "sha256:aaaa" });
        store.Register(new MeshRegistration { Service = "payments", ServiceVersion = "2", RegistrationHash = "sha256:bbbb" });

        // v1 reporting v2's hash: v2 registered it, but this instance is v1 - a mismatch, where comparing
        // with any registered version would have read it as a match.
        store.Heartbeat(new MeshHeartbeat { Service = "payments", ServiceVersion = "1", InstanceId = "a", RegistrationHash = "sha256:bbbb" });
        store.Heartbeat(new MeshHeartbeat { Service = "payments", ServiceVersion = "2", InstanceId = "b", RegistrationHash = "sha256:bbbb" });
        // A version no document registered: an unregistered instance, not a mismatch.
        store.Heartbeat(new MeshHeartbeat { Service = "payments", ServiceVersion = "3", InstanceId = "c", RegistrationHash = "sha256:cccc" });

        var instances = store.Service("payments")!.Instances.ToDictionary(x => x.InstanceId);
        Assert.False(instances["a"].HashMatches);
        Assert.True(instances["b"].HashMatches);
        Assert.Null(instances["c"].HashMatches);
        Assert.Equal("1", instances["a"].ServiceVersion);
        Assert.Equal("sha256:bbbb", instances["a"].RegistrationHash);
    }

    [Fact]
    public void ARegistration_IsNeverRejectedOrRecomputedForItsHash()
    {
        // mesh.md §2: the carried registrationHash is trusted - a fake value is stored and compared as is.
        var store = new MeshCollectorStore();
        store.Register(new MeshRegistration { Service = "orders", RegistrationHash = "sha256:aaaa", Requests = Entries(new[] { "order:create" }) });
        store.Heartbeat(new MeshHeartbeat { Service = "orders", InstanceId = "i1", RegistrationHash = "sha256:aaaa" });

        Assert.True(Assert.Single(store.Service("orders")!.Instances).HashMatches);
    }

    // ---- correlation lookup (mesh:query:correlation, mesh-product-owner ruling 2026-07-23) ----

    private static MeshTraceEvent CorrEvent(string traceId, string spanId, string service, string topic,
        DateTimeOffset startedAt, string? correlationId, string status = "ok")
    {
        var evt = Event(traceId, spanId, service, topic, startedAt, status);
        evt.CorrelationId = correlationId;
        return evt;
    }

    [Fact]
    public void Correlation_GroupsMatchingFlowsByTrace_OrderedByEarliestStart_EventsInStartOrder()
    {
        // One business correlation id spans two distinct traces; a third trace carries a different id.
        var store = new MeshCollectorStore();
        var now = DateTimeOffset.UtcNow;
        store.AddEvents(new[]
        {
            // trace-b starts later but its events are added first, to prove ordering is by StartedAt.
            CorrEvent("trace-b", "b2", "shipping", "book", now.AddSeconds(10).AddMilliseconds(5), "corr-1"),
            CorrEvent("trace-b", "b1", "orders", "ship", now.AddSeconds(10), "corr-1"),
            CorrEvent("trace-a", "a1", "orders", "create", now, "corr-1"),
            CorrEvent("trace-a", "a2", "payments", "capture", now.AddMilliseconds(5), "corr-1", status: "service-unavailable"),
            CorrEvent("trace-c", "c1", "orders", "create", now.AddSeconds(20), "other"),
        });

        var view = store.Correlation("corr-1");

        Assert.NotNull(view);
        Assert.Equal("corr-1", view!.CorrelationId);
        Assert.Equal(2, view.Traces.Count);
        // Traces ordered by earliest event start: trace-a (now) before trace-b (now+10s).
        Assert.Equal("trace-a", view.Traces[0].TraceId);
        Assert.Equal("trace-b", view.Traces[1].TraceId);
        // Events within a trace in start order (b1 before b2 despite reversed insertion).
        Assert.Equal(new[] { "a1", "a2" }, view.Traces[0].Events.Select(e => e.SpanId).ToArray());
        Assert.Equal(new[] { "b1", "b2" }, view.Traces[1].Events.Select(e => e.SpanId).ToArray());
        // The per-leg service/topic/status the owner wants to read survives intact.
        Assert.Equal("payments", view.Traces[0].Events[1].Service);
        Assert.Equal("service-unavailable", view.Traces[0].Events[1].Status);
    }

    [Fact]
    public void Correlation_ExcludesNullCorrelationEvents_AndReturnsNullWhenNothingMatches()
    {
        // The mesh never fabricates a correlation id: a flow whose entry set no x-correlation-id
        // header simply won't appear in any lookup.
        var store = new MeshCollectorStore();
        var now = DateTimeOffset.UtcNow;
        store.AddEvents(new[]
        {
            CorrEvent("trace-1", "s1", "orders", "create", now, correlationId: null),
        });

        Assert.Null(store.Correlation("corr-1"));
    }

    [Fact]
    public async Task CorrelationQueryHandler_EmptyId_BadRequest_UnknownId_NotFound_KnownId_Ok()
    {
        var store = new MeshCollectorStore();
        store.AddEvents(new[] { CorrEvent("trace-1", "s1", "orders", "create", DateTimeOffset.UtcNow, "corr-1") });
        var handler = new CorrelationQueryMessageHandler(store);

        Assert.Equal("bad-request", (await handler.HandleAsync(new CorrelationQuery { CorrelationId = "" })).Status);
        Assert.Equal("not-found", (await handler.HandleAsync(new CorrelationQuery { CorrelationId = "nope" })).Status);
        var ok = await handler.HandleAsync(new CorrelationQuery { CorrelationId = "corr-1" });
        Assert.Equal("ok", ok.Status);
        var okPayload = Assert.IsType<CorrelationView>(ok.Payload);
        Assert.Equal("corr-1", okPayload.CorrelationId);
        Assert.Single(okPayload.Traces);
    }

    // ---- #234: no missing feed ever fails ingestion (spec §6), for whole wire-supplied lists ----
    //
    // A wire payload can deserialize an explicit-null list into an actual null (nullable-reference
    // annotations aren't enforced at runtime) - matching how Go's encoding/json marshals a nil slice.
    // Before the fix, Register/AddEvents/AddIssues all threw NullReferenceException on this; the spec's
    // collector contract requires it be accepted as empty instead.

    [Fact]
    public void Register_NullRequestsAndSends_IsAcceptedAsUnknownMembers()
    {
        var registration = System.Text.Json.JsonSerializer.Deserialize<MeshRegistration>(
            "{\"service\":\"svc\",\"requests\":null,\"sends\":null}", MeshJson.Options)!;
        var store = new MeshCollectorStore();

        store.Register(registration);

        var view = store.Service("svc");
        Assert.NotNull(view);
        Assert.Equal(0, view!.Topics); // read back with no NRE
        Assert.NotNull(view.Registration);
        Assert.False(view.Registration!.IsKnown(MeshRegistrationMembers.Requests));
        Assert.False(view.Registration.IsKnown(MeshRegistrationMembers.Sends));
    }

    [Fact]
    public void AddEvents_NullEventsList_IsAcceptedAsANoOpBatch()
    {
        var batch = System.Text.Json.JsonSerializer.Deserialize<MeshTraceBatch>(
            "{\"events\":null}", MeshJson.Options)!;
        var store = new MeshCollectorStore();

        var accepted = store.AddEvents(batch.Events);

        Assert.Equal(0, accepted);
    }

    // ---- #253: no missing feed ever fails ingestion (spec §6), one level down from #234 - a null
    // ELEMENT inside a non-null list (legal on the wire: MeshTraceEvent/MeshIssue are reference
    // types) must be skipped, not dereferenced, so it can't partially corrupt a batch's ingestion.

    [Fact]
    public void AddEvents_NullElementInEventsList_DoesNotThrow_AndAppliesTheOtherEvents()
    {
        var before = Event("trace-1", "span-1", "svc", "topic", DateTimeOffset.UtcNow);
        var after = Event("trace-2", "span-2", "svc", "topic", DateTimeOffset.UtcNow.AddMilliseconds(1));
        var store = new MeshCollectorStore();

        var accepted = store.AddEvents(new[] { before, null!, after });

        // No NullReferenceException, both real events ingested. Accepted counts only the real
        // events processed (matching AddIssues' established convention: an entry that's skipped
        // rather than stored - here, a null slot - is never counted as accepted).
        Assert.Equal(2, accepted);
        var topic = store.Topic("topic", null);
        Assert.NotNull(topic);
        Assert.Equal(2, topic!.Invocations);
    }

    [Fact]
    public void AddIssues_NullElementInIssuesList_DoesNotThrow_AndAppliesTheOtherIssues()
    {
        var store = new MeshCollectorStore();
        var real = new MeshIssue { Fingerprint = "fp-1", Topic = "topic", FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow, Count = 1 };
        var batch = new MeshIssueBatch { Service = "svc", Issues = new List<MeshIssue> { real, null! } };

        var accepted = store.AddIssues(batch);

        Assert.Equal(1, accepted);
        Assert.Single(store.Fleet().Issues);
    }

    [Fact]
    public void AddIssues_NullIssuesList_IsAcceptedAsALivenessOnlyBatch_AndMarksTheFeedWired()
    {
        var store = new MeshCollectorStore();
        // A service with failing traffic and no issues batch yet would report "issues" as a missing
        // feed (ServiceSummaryLocked) - the null-tolerant liveness batch below must still clear that.
        store.AddEvents(new[] { Event("trace-1", "span-1", "svc", "topic", DateTimeOffset.UtcNow, status: "unexpected-error") });

        var batch = System.Text.Json.JsonSerializer.Deserialize<MeshIssueBatch>(
            "{\"service\":\"svc\",\"issues\":null}", MeshJson.Options)!;
        var accepted = store.AddIssues(batch);

        Assert.Equal(0, accepted);
        var fleet = store.Fleet();
        var summary = Assert.Single(fleet.Services, s => s.Service == "svc");
        Assert.DoesNotContain("issues", summary.MissingFeeds);
    }

    // ---- #283: RecordObservedActivityAndDrift must be version-aware like HashMatches - a topic
    // declared by ANY currently live version of a service is "declared", not just the most-recently-
    // registered ("headline") version's own list. Without this, the moment a second version
    // registers, every message an older-but-still-live version legitimately handles is misfiled as
    // contract-drift - the same false-positive class #251 fixed for HashMatches, 90 lines above.

    [Fact]
    public void OlderLiveVersion_HandlingItsOwnDeclaredTopic_IsNotFlaggedAsDrift_OnceANewerVersionRegisters()
    {
        var store = new MeshCollectorStore();
        var v1 = Registration("orders", requests: new[] { "topic-a" }, serviceVersion: "1.0.0");
        var v2 = Registration("orders", requests: new[] { "topic-b" }, serviceVersion: "2.0.0");
        store.Register(v1);
        store.Register(v2); // v2 becomes CurrentVersionKey - the "headline" row

        // A v1 instance handles topic-a - exactly what v1's OWN live, registered document declares.
        store.AddEvents(new[] { Event("trace-1", "span-1", "orders", "topic-a", DateTimeOffset.UtcNow) });

        Assert.Empty(store.Fleet().Issues);
    }

    // ---- spec §2.4: re-registering one version must not disturb a sibling version's edges ----
    //
    // Edges hold service NAMES (graph membership is by service), so retracting one version's claims
    // must leave a name in place while another live version of that service still declares the
    // topic. Before this, re-registering v1 without topic-a erased the consumer edge v2 still declared
    // - the mesh-service-version-cases.json "re-registering one version does not disturb the other"
    // case.

    [Fact]
    public void ReregisteringOneVersion_WithoutATopic_KeepsTheEdgeASiblingVersionStillDeclares()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payment:take" }, sends: new[] { "ledger:post" }, serviceVersion: "1"));
        store.Register(Registration("payments", requests: new[] { "payment:take" }, sends: new[] { "ledger:post" }, serviceVersion: "2"));

        store.Register(Registration("payments", serviceVersion: "1")); // v1 redeploys declaring nothing

        Assert.Equal(new[] { "payments" }, store.Topic("payment:take", null)!.Consumers);
        Assert.Equal(new[] { "payments" }, store.Topic("ledger:post", null)!.Providers);
    }

    [Fact]
    public void ReregisteringTheOnlyVersionThatDeclaredATopic_StillRetractsTheEdge()
    {
        var store = new MeshCollectorStore();
        store.Register(Registration("payments", requests: new[] { "payment:take", "payment:refund" }, serviceVersion: "1"));
        store.Register(Registration("payments", requests: new[] { "payment:take" }, serviceVersion: "2"));

        store.Register(Registration("payments", requests: new[] { "payment:take" }, serviceVersion: "1"));

        Assert.Empty(store.Topic("payment:refund", null)!.Consumers); // no live version declares it now
        Assert.Equal(new[] { "payments" }, store.Topic("payment:take", null)!.Consumers);
    }

    [Fact]
    public void TopicNoLiveVersionDeclares_IsStillFlaggedAsDrift_EvenWithMultipleLiveVersions()
    {
        var store = new MeshCollectorStore();
        var v1 = Registration("orders", requests: new[] { "topic-a" }, serviceVersion: "1.0.0");
        var v2 = Registration("orders", requests: new[] { "topic-b" }, serviceVersion: "2.0.0");
        store.Register(v1);
        store.Register(v2);

        // Neither live version declares "topic-c" - a genuine drift must still be caught.
        store.AddEvents(new[] { Event("trace-1", "span-1", "orders", "topic-c", DateTimeOffset.UtcNow) });

        var issue = Assert.Single(store.Fleet().Issues);
        Assert.Equal(MeshIssueClassification.ContractDrift, issue.Classification);
        Assert.Equal("topic-c", issue.Topic);
    }

    // ---- #290: ServiceState.Registrations has no eviction policy - a service that legitimately
    // re-registers under a new ServiceVersion on every deploy accumulates one permanent entry per
    // historical deploy, forever. A max-versions-per-service cap (default 8) bounds it, evicting the
    // least-recently-registered non-current version (preferring one with no live instance) and
    // retracting its topic edges so nothing dangles.

    private static int RegistrationCountFor(MeshCollectorStore store, string serviceName)
    {
        var servicesField = typeof(MeshCollectorStore)
            .GetField("_services", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var services = (System.Collections.IDictionary)servicesField.GetValue(store)!;
        var state = services[serviceName]!;
        var descriptorsField = state.GetType()
            .GetField("Registrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var descriptors = (System.Collections.IDictionary)descriptorsField.GetValue(state)!;
        return descriptors.Count;
    }

    [Fact]
    public void Register_ManyDistinctVersions_RegistrationsAreCappedAtTheConfiguredMax()
    {
        var store = new MeshCollectorStore(maxVersionsPerService: 8);
        const int deployCount = 5000; // simulates 5,000 CI/CD deploys of one logical service
        for (var i = 0; i < deployCount; i++)
        {
            store.Register(Registration("orders-api", requests: new[] { $"topic-{i}" }, serviceVersion: $"deploy-{i}"));
        }

        Assert.Equal(8, RegistrationCountFor(store, "orders-api"));
    }

    [Fact]
    public void Register_OverCap_EvictsTheLeastRecentlyRegisteredVersionWithNoLiveInstance_NotACurrentOrLiveOne()
    {
        var store = new MeshCollectorStore(maxVersionsPerService: 2);
        store.Register(new MeshRegistration
        {
            Service = "orders", ServiceVersion = "v1", RegistrationHash = "hash-v1",
            Requests = Entries(new[] { "topic-old" })
        });
        // v1 has a live instance heartbeating it - it must survive eviction in favor of a dead version.
        store.Heartbeat(new MeshHeartbeat { Service = "orders", ServiceVersion = "v1", InstanceId = "i1", RegistrationHash = "hash-v1" });
        store.Register(new MeshRegistration
        {
            Service = "orders", ServiceVersion = "v2", RegistrationHash = "hash-v2",
            Requests = Entries(new[] { "topic-mid" })
        }); // no instance ever heartbeats v2

        // Registering v3 exceeds the cap of 2: v2 (no live instance) must be evicted over v1 (live instance).
        store.Register(new MeshRegistration
        {
            Service = "orders", ServiceVersion = "v3", RegistrationHash = "hash-v3",
            Requests = Entries(new[] { "topic-new" })
        });

        Assert.Equal(2, RegistrationCountFor(store, "orders"));
        // v1's edge survives...
        Assert.Equal(new List<string> { "orders" }, store.Topic("topic-old", null)!.Consumers);
        // ...v2's edge was retracted (evicted), not left dangling in the topic catalog...
        Assert.Empty(store.Topic("topic-mid", null)!.Consumers);
        // ...and v3 (the newly registered, current version) is present.
        Assert.Equal(new List<string> { "orders" }, store.Topic("topic-new", null)!.Consumers);
    }

    [Fact]
    public void Register_OverCap_NeverEvictsTheCurrentVersion()
    {
        var store = new MeshCollectorStore(maxVersionsPerService: 1);
        store.Register(Registration("orders", requests: new[] { "topic-a" }, serviceVersion: "v1"));

        // Registering v2 exceeds the cap of 1 - v1 (the only other version) is evicted, never v2
        // itself (the version just registered, which becomes current).
        store.Register(Registration("orders", requests: new[] { "topic-b" }, serviceVersion: "v2"));

        Assert.Equal(1, RegistrationCountFor(store, "orders"));
        var summary = store.Fleet().Services.Single(s => s.Service == "orders");
        Assert.Equal("v2", summary.ServiceVersion);
        Assert.Empty(store.Topic("topic-a", null)!.Consumers); // v1's edge retracted on eviction
    }
}
