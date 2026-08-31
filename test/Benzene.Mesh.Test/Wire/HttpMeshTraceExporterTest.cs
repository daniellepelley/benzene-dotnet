using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Test.Mesh.Wire;

/// <summary>
/// Regression coverage for #233: <see cref="HttpMeshTraceExporter.PumpAsync"/> used to recreate its
/// wait-timeout deadline every loop iteration, so any channel activity before the timeout fired reset
/// the effective countdown - a steady trickle below <c>batchSize</c> never reached a time-based flush
/// at all, only process shutdown did. The fix tracks an absolute next-flush deadline that can't be
/// pushed back by activity.
/// </summary>
public class HttpMeshTraceExporterTest
{
    private static MeshTraceEvent Event(string traceId) => new()
    {
        TraceId = traceId,
        SpanId = traceId,
        Service = "svc",
        Topic = "topic",
        Status = "ok",
        DurationMs = 1,
        StartedAt = DateTimeOffset.UtcNow
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task PumpAsync_SteadyTrickleBelowBatchSize_StillFlushesOnTheTimeDeadline()
    {
        // The review's exact probe, shrunk so the test runs in a few seconds: a steady trickle well
        // below batchSize, against a short flushInterval. Before the fix this produced ZERO POSTs
        // until DisposeAsync's tail flush; after the fix, at least one lands from the deadline alone
        // while events keep trickling in.
        var handler = new CountingHandler();
        await using var exporter = new HttpMeshTraceExporter(
            new HttpClient(handler),
            "http://mesh.internal/mesh/envelope",
            batchSize: 64,
            flushInterval: TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < 12; i++)
        {
            exporter.Export(Event($"trace-{i}"));
            await Task.Delay(50); // 1 event / 50ms - a steady trickle, well below batchSize
        }

        // ~2x flushInterval beyond the trickle's own elapsed time, well before DisposeAsync's tail
        // flush would otherwise be the only thing that could produce a POST.
        var flushedOnDeadline = await WaitUntilAsync(() => handler.Posts > 0, TimeSpan.FromSeconds(2));

        Assert.True(flushedOnDeadline, "expected a time-based flush while events were still trickling in, not only at shutdown");
    }

    [Fact]
    public async Task PumpAsync_BatchFull_StillFlushesEarlyWithoutWaitingForTheDeadline()
    {
        var handler = new CountingHandler();
        await using var exporter = new HttpMeshTraceExporter(
            new HttpClient(handler),
            "http://mesh.internal/mesh/envelope",
            batchSize: 4,
            flushInterval: TimeSpan.FromSeconds(30)); // long enough that only batch-fill could flush in time

        for (var i = 0; i < 4; i++)
        {
            exporter.Export(Event($"trace-{i}"));
        }

        var flushedOnBatchFull = await WaitUntilAsync(() => handler.Posts > 0, TimeSpan.FromSeconds(2));

        Assert.True(flushedOnBatchFull, "expected the batch-full path to flush without waiting for the (long) deadline");
    }

    [Fact]
    public async Task DisposeAsync_StillTailFlushesAnyRemainingBufferOnShutdown()
    {
        var handler = new CountingHandler();
        var exporter = new HttpMeshTraceExporter(
            new HttpClient(handler),
            "http://mesh.internal/mesh/envelope",
            batchSize: 64,
            flushInterval: TimeSpan.FromSeconds(30)); // long enough that only shutdown could flush in time

        exporter.Export(Event("trace-only"));

        await exporter.DisposeAsync();

        Assert.True(handler.Posts > 0, "expected the shutdown tail-flush to still send the buffered event");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
