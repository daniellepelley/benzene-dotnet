using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Clients.TraceContext;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.ResponseEvents;
using Benzene.Results;
using Benzene.Test.ResponseEvents;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients;

/// <summary>
/// mesh.md §3 / §7.1 M2: a downstream Benzene message - a send, one-way or with a reply, or a published
/// event - carries <c>traceparent: 00-&lt;traceId&gt;-&lt;spanId&gt;-01</c> built from the calling
/// invocation's span: the <c>spanId</c> its TraceEvent records, "whatever other tracing the port also runs",
/// so the downstream TraceEvent names it as <c>parentSpanId</c>. Two traced services, end to end in process:
/// the upstream sends (or publishes) through an outbound route that delivers into the downstream's pipeline.
/// </summary>
public class MeshTraceContextForwardingTest
{
    private const string InboundTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string InboundTraceparent = "00-" + InboundTraceId + "-00f067aa0ba902b7-01";
    private const string InboundTracestate = "vendor=upstream-caller";

    private sealed class RecordingExporter : IMeshTraceExporter
    {
        public List<MeshTraceEvent> Events { get; } = new();

        public void Export(MeshTraceEvent traceEvent) => Events.Add(traceEvent);
    }

    /// <summary>The downstream service: traced, answering ok. Returns the headers it received too.</summary>
    private sealed class Downstream
    {
        private readonly BenzeneMessageApplication _application;
        private readonly MicrosoftServiceResolverFactory _factory;

        public Downstream()
        {
            var services = new ServiceCollection();
            services.UsingBenzene(x => x.AddBenzene().AddBenzeneMessage().AddContextItems());
            var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(services));
            pipeline
                .UseMeshTrace(new MeshServiceInfo("payments"), Exporter, new BenzeneMessageMeshStatusReader())
                .Use(resolver => new TerminalFuncWrapperMiddleware<BenzeneMessageContext>("Ok", async (context, _) =>
                {
                    var topic = resolver.GetService<Benzene.Abstractions.MessageHandlers.Mappers.IMessageGetter<BenzeneMessageContext>>().GetTopic(context);
                    await resolver.GetService<Benzene.Abstractions.MessageHandlers.Mappers.IMessageHandlerResultSetter<BenzeneMessageContext>>()
                        .SetResultAsync(context, new MessageHandlerResult(topic, MessageHandlerDefinition.Empty(), BenzeneResult.Ok()));
                }));
            _application = new BenzeneMessageApplication(pipeline.Build());
            _factory = new MicrosoftServiceResolverFactory(services);
        }

        public RecordingExporter Exporter { get; } = new();

        public List<IDictionary<string, string>> ReceivedHeaders { get; } = new();

        // The route's transport: deliver the send into this service's envelope pipeline, as an HTTP or queue
        // hop would - the headers are the native metadata channel.
        public void Deliver(IMiddlewarePipelineBuilder<OutboundContext> route)
        {
            route.Use(_ => new TerminalFuncWrapperMiddleware<OutboundContext>("Deliver", async (context, _) =>
            {
                var headers = new Dictionary<string, string>(context.Headers);
                ReceivedHeaders.Add(headers);
                await _application.HandleAsync(new BenzeneMessageRequest { Topic = context.Topic, Headers = headers, Body = "{}" }, _factory);
                context.Response = BenzeneResult.Accepted<Void>();
            }));
        }
    }

    private static async Task<RecordingExporter> RunUpstreamSendAsync(Action<OutboundRoutingBuilder> configureRouting)
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        services.UsingBenzene(x =>
        {
            x.AddBenzeneMessage();
            x.AddOutboundRouting(configureRouting);
        });
        var upstream = new RecordingExporter();
        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(services));
        pipeline
            .UseMeshTrace(new MeshServiceInfo("orders"), upstream, new BenzeneMessageMeshStatusReader())
            .Use(resolver => new TerminalFuncWrapperMiddleware<BenzeneMessageContext>("SendThenOk", async (context, _) =>
            {
                // The handler's send - a one-way send to a topic another service defines.
                var sent = await resolver.GetService<IBenzeneMessageSender>()
                    .SendAsync<ExampleRequestPayload, Void>("payments:capture", new ExampleRequestPayload { Id = 1 });
                Assert.True(sent.IsSuccessful);
                await resolver.GetService<Benzene.Abstractions.MessageHandlers.Mappers.IMessageHandlerResultSetter<BenzeneMessageContext>>()
                    .SetResultAsync(context, new MessageHandlerResult(new Benzene.Core.Messages.Topic("order:create"), MessageHandlerDefinition.Empty(), BenzeneResult.Ok()));
            }));

        await new BenzeneMessageApplication(pipeline.Build()).HandleAsync(new BenzeneMessageRequest
        {
            Topic = "order:create",
            Headers = new Dictionary<string, string> { ["traceparent"] = InboundTraceparent, ["tracestate"] = InboundTracestate },
            Body = "{}"
        }, new MicrosoftServiceResolverFactory(services.BuildServiceProvider()));

        return upstream;
    }

    private static void AssertJoined(RecordingExporter upstream, Downstream downstream)
    {
        var upstreamEvent = Assert.Single(upstream.Events);
        var downstreamEvent = Assert.Single(downstream.Exporter.Events);

        // The point of M2: the downstream TraceEvent's parent is the upstream TraceEvent's own span.
        Assert.Equal(upstreamEvent.SpanId, downstreamEvent.ParentSpanId);
        Assert.Equal(upstreamEvent.TraceId, downstreamEvent.TraceId);
        Assert.Equal(InboundTraceId, downstreamEvent.TraceId);

        var headers = Assert.Single(downstream.ReceivedHeaders);
        Assert.Equal($"00-{upstreamEvent.TraceId}-{upstreamEvent.SpanId}-01", headers["traceparent"]);
        // tracestate carried forward unchanged (wire-contracts §2).
        Assert.Equal(InboundTracestate, headers["tracestate"]);
    }

    [Fact]
    public async Task ASend_ThroughARoutingThatForwardsTheMeshSpan_ParentsTheDownstreamTraceEventOnTheUpstreamOne()
    {
        var downstream = new Downstream();

        var upstream = await RunUpstreamSendAsync(routing => routing
            .UseMeshTraceContext()
            .Route("payments:capture", downstream.Deliver));

        AssertJoined(upstream, downstream);
    }

    [Fact]
    public async Task ARouteLevelUseMeshTraceContext_ForwardsTheMeshSpanToo()
    {
        var downstream = new Downstream();

        var upstream = await RunUpstreamSendAsync(routing => routing
            .Route("payments:capture", route =>
            {
                route.UseMeshTraceContext();
                downstream.Deliver(route);
            }));

        AssertJoined(upstream, downstream);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithOpenTelemetryAlsoForwarding_TheMeshSpanStillWins_WhicheverRunsFirst(bool meshFirst)
    {
        // "whatever other tracing the port also runs": an Activity is current, and the route also runs
        // UseW3CTraceContext(), which would forward Activity.Current - a different span.
        using var source = new ActivitySource("Test." + nameof(MeshTraceContextForwardingTest));
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("otel")!;
        activity.TraceStateString = "otel=1";
        var downstream = new Downstream();

        var upstream = await RunUpstreamSendAsync(routing => routing
            .Route("payments:capture", route =>
            {
                if (meshFirst)
                {
                    route.UseMeshTraceContext().UseW3CTraceContext();
                }
                else
                {
                    route.UseW3CTraceContext().UseMeshTraceContext();
                }

                downstream.Deliver(route);
            }));

        AssertJoined(upstream, downstream);
        Assert.NotEqual(activity.Id, downstream.ReceivedHeaders.Single()["traceparent"]);
    }

    [Fact]
    public async Task UseW3CTraceContextAlone_ForwardsActivityCurrent_NotTheMeshSpan()
    {
        // Why the mesh needs its own middleware: OpenTelemetry's span is not the TraceEvent's.
        using var source = new ActivitySource("Test." + nameof(UseW3CTraceContextAlone_ForwardsActivityCurrent_NotTheMeshSpan));
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("otel")!;
        var downstream = new Downstream();

        var upstream = await RunUpstreamSendAsync(routing => routing
            .Route("payments:capture", route => downstream.Deliver(route.UseW3CTraceContext())));

        Assert.Equal(activity.Id, downstream.ReceivedHeaders.Single()["traceparent"]);
        Assert.NotEqual(upstream.Events.Single().SpanId, downstream.Exporter.Events.Single().ParentSpanId);
    }

    [Fact]
    public async Task NoMeshSpan_NothingIsStamped()
    {
        // Degrade, never fabricate: a send outside a traced invocation carries no traceparent.
        var downstream = new Downstream();
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(routing => routing
            .UseMeshTraceContext()
            .Route("payments:capture", downstream.Deliver));
        var sender = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();

        await sender.SendAsync<ExampleRequestPayload, Void>("payments:capture", new ExampleRequestPayload());

        Assert.False(downstream.ReceivedHeaders.Single().ContainsKey("traceparent"));
        Assert.Null(downstream.Exporter.Events.Single().ParentSpanId);
    }

    [Fact]
    public async Task APublishedEvent_IsParentedOnThePublishingInvocationsSpan_LikeASend()
    {
        var downstream = new Downstream();
        var services = ServiceResolverMother.CreateServiceCollection();
        services.UsingBenzene(x =>
        {
            x.AddBenzeneMessage();
            x.AddOutboundRouting(routing => routing
                .UseMeshTraceContext()
                .Route("order:created", downstream.Deliver));
        });
        var upstream = new RecordingExporter();
        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(services));
        pipeline
            .UseMeshTrace(new MeshServiceInfo("orders"), upstream, new BenzeneMessageMeshStatusReader())
            .UseMessageHandlers(new[] { typeof(ResponseEventsCreateOrderHandler) }, router => router
                .UseResponseEvents(events => events.Map("order:create", "order:created")));

        await new BenzeneMessageApplication(pipeline.Build()).HandleAsync(new BenzeneMessageRequest
        {
            Topic = "order:create",
            Headers = new Dictionary<string, string> { ["traceparent"] = InboundTraceparent, ["tracestate"] = InboundTracestate },
            Body = JsonConvert.SerializeObject(new ExampleRequestPayload { Id = 42, Name = "created" })
        }, new MicrosoftServiceResolverFactory(services.BuildServiceProvider()));

        AssertJoined(upstream, downstream);
    }
}
