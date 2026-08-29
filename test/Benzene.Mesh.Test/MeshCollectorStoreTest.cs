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

    private static MeshServiceDescriptor Descriptor(
        string service, string[]? topics = null, string[]? produces = null, string? serviceVersion = null)
    {
        return new MeshServiceDescriptor
        {
            Service = service,
            Topics = (topics ?? Array.Empty<string>()).Select(id => new MeshTopicDescriptor { Id = id }).ToList(),
            Produces = (produces ?? Array.Empty<string>()).Select(id => new MeshTopicDescriptor { Id = id }).ToList(),
            ServiceVersion = serviceVersion
        };
    }

    // ---- declared graph (spec §4): the SOLE source of producer/consumer edges ----
    //
    // Role assignment, since the 2026-08 inversion: `topics` (the topics a service HANDLES) makes it
    // a CONSUMER of them; `produces` (its outbound registration) makes it a PROVIDER. That is the
    // way every broker in the field uses the words, and the opposite of what this file asserted
    // before the inversion.

    [Fact]
    public void Graph_IsDeclaredFromTopicsAndProduces_ZeroTrafficStillReportsTheFullGraph()
    {
        var store = new MeshCollectorStore();
        // payments HANDLES payments:capture, so it CONSUMES it. orders SENDS it, so it PROVIDES it.
        store.Register(Descriptor("payments", topics: new[] { "payments:capture" }));
        store.Register(Descriptor("orders", topics: new[] { "order:create" }, produces: new[] { "payments:capture" }));

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
        store.Register(Descriptor("greeter", topics: new[] { "greet" }));
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
        store.Register(Descriptor("payments", topics: new[] { "payments:capture" }));
        store.Register(Descriptor("orders", topics: new[] { "order:create" }, produces: new[] { "payments:capture" }));
        Assert.Equal(new List<string> { "orders" }, store.Topic("payments:capture", null)!.Providers);

        // orders redeploys and drops both the topic it handled and the one it produced.
        store.Register(Descriptor("orders", topics: new[] { "order:cancel" }));

        Assert.Empty(store.Topic("order:create", null)!.Consumers);
        Assert.Empty(store.Topic("payments:capture", null)!.Providers);
        Assert.Equal(new List<string> { "orders" }, store.Topic("order:cancel", null)!.Consumers);
    }

    // ---- ServiceVersion (mesh.md §2.5): retained on ingest, exposed on both query results ----

    [Fact]
    public void Register_WithServiceVersion_IsRetainedAndExposedOnFleetAndServiceQueries()
    {
        var store = new MeshCollectorStore();
        store.Register(Descriptor("orders", topics: new[] { "order:create" }, serviceVersion: "2.3.1"));

        var summary = store.Fleet().Services.Single(s => s.Service == "orders");
        var view = store.Service("orders", null);

        Assert.Equal("2.3.1", summary.ServiceVersion);
        Assert.NotNull(view);
        Assert.Equal("2.3.1", view!.ServiceVersion);
        Assert.Equal("2.3.1", view.Descriptor?.ServiceVersion); // still on the full descriptor too
    }

    [Fact]
    public void Register_WithNoServiceVersion_LeavesItNull_NotDefaultedOrDropped()
    {
        var store = new MeshCollectorStore();
        store.Register(Descriptor("orders", topics: new[] { "order:create" })); // no serviceVersion

        var summary = store.Fleet().Services.Single(s => s.Service == "orders");

        Assert.Null(summary.ServiceVersion);
    }

    [Fact]
    public void Reregistration_ReplacesServiceVersion_WithTheLatestDescriptors()
    {
        var store = new MeshCollectorStore();
        store.Register(Descriptor("orders", topics: new[] { "order:create" }, serviceVersion: "1.0.0"));
        store.Register(Descriptor("orders", topics: new[] { "order:create" }, serviceVersion: "2.0.0"));

        var summary = store.Fleet().Services.Single(s => s.Service == "orders");

        Assert.Equal("2.0.0", summary.ServiceVersion);
    }

    // ---- §4.2 declared vs. observed: liveness ("Unobserved") ----

    [Fact]
    public void DeclaredEdge_WithNoMatchingTrace_ReportsAbsentLastObservedAt()
    {
        var store = new MeshCollectorStore();
        store.Register(Descriptor("payments", topics: new[] { "payments:capture" }));
        store.Register(Descriptor("orders", topics: new[] { "order:create" }, produces: new[] { "payments:capture" }));

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
        store.Register(Descriptor("payments", topics: new[] { "payments:capture" }));
        store.Register(Descriptor("orders", topics: new[] { "order:create" }, produces: new[] { "payments:capture" }));
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
        store.Register(Descriptor("greeter", topics: Array.Empty<string>()));

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
        store.Register(Descriptor("payments", topics: new[] { "payments:capture" }));
        // "orders" registers but never declares "payments:capture" in its produces.
        store.Register(Descriptor("orders", topics: new[] { "order:create" }));
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
        store.Register(Descriptor("greeter", topics: Array.Empty<string>()));
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
    public void DegradedDescriptor_IsNeverFlaggedAsDrift()
    {
        // A service that HAS registered but honestly marked its registry/outbound-registry as
        // degraded doesn't know its own topics/produces yet - flagging it would be a false positive.
        var store = new MeshCollectorStore();
        store.Register(new MeshServiceDescriptor { Service = "greeter", Degraded = new List<string> { "registry" } });

        store.AddEvents(new[] { Event("trace-1", "span-1", "greeter", "greet", DateTimeOffset.UtcNow) });

        Assert.Empty(store.Fleet().Issues);
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
    public void Register_NullTopicsAndProduces_IsAcceptedAsAnEmptyDeclaredGraph()
    {
        var descriptor = System.Text.Json.JsonSerializer.Deserialize<MeshServiceDescriptor>(
            "{\"service\":\"svc\",\"topics\":null,\"produces\":null}", MeshJson.Options)!;
        var store = new MeshCollectorStore();

        store.Register(descriptor);

        var view = store.Service("svc");
        Assert.NotNull(view);
        Assert.Equal(0, view!.Topics); // Descriptor.Topics.Count read back with no NRE
        Assert.NotNull(view.Descriptor);
        Assert.Empty(view.Descriptor!.Topics);
        Assert.Empty(view.Descriptor.Produces);
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
}
