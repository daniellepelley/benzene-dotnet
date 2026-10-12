using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Benzene.Clients;
using Benzene.Clients.TraceContext;
using Xunit;

namespace Benzene.Test.Clients;

public class W3CTraceContextMiddlewareTest
{
    [Fact]
    public async Task HandleAsync_DoesNotMutateTheCallersHeaderDictionary()
    {
        // OutboundContext copies the caller's headers so the outbound middleware can't leak state
        // across sends that reuse one dictionary (a stale traceparent/tracestate contaminating the
        // next send). Stamping trace context must touch only the context's own copy.
        using var activitySource = new ActivitySource("Test.Copy." + nameof(W3CTraceContextMiddlewareTest));
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("outbound-call");

        var callerHeaders = new Dictionary<string, string>();
        var context = new OutboundContext("my-topic", "hello", callerHeaders);
        var middleware = new W3CTraceContextMiddleware();

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.True(context.Headers.ContainsKey("traceparent")); // stamped on the copy
        Assert.False(callerHeaders.ContainsKey("traceparent"));  // caller's dict untouched
    }

    [Fact]
    public async Task HandleAsync_ActiveActivity_StampsTraceparentOntoContextHeaders()
    {
        using var activitySource = new ActivitySource("Test." + nameof(W3CTraceContextMiddlewareTest));
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("outbound-call");

        var context = new OutboundContext("my-topic", "hello");
        var middleware = new W3CTraceContextMiddleware();
        var nextCalled = false;

        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Equal(activity.Id, context.Headers["traceparent"]);
    }

    [Fact]
    public async Task HandleAsync_NoAmbientActivity_LeavesHeadersUnchanged()
    {
        var context = new OutboundContext("my-topic", "hello");
        var middleware = new W3CTraceContextMiddleware();

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.False(context.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public async Task HandleAsync_APinnedTraceContext_IsLeftUntouched()
    {
        // The mesh pins the span its TraceEvent records (mesh.md §3 / M2); OpenTelemetry's Activity.Current
        // must not overwrite it.
        using var activitySource = new ActivitySource("Test.Pinned." + nameof(W3CTraceContextMiddlewareTest));
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("outbound-call");
        activity!.TraceStateString = "otel=1";

        var context = new OutboundContext("my-topic", "hello", new Dictionary<string, string> { ["tracestate"] = "stale=1" });
        OutboundTraceContext.Pin(context, "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", null);

        await new W3CTraceContextMiddleware().HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", context.Headers["traceparent"]);
        // Pinning without a tracestate drops a stale one rather than forwarding another parent's.
        Assert.False(context.Headers.ContainsKey("tracestate"));
    }
}
