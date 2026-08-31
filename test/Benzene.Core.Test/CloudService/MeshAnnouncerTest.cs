using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.CloudService;
using Benzene.HealthChecks.Core;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Test.CloudService;

/// <summary>
/// #47: <c>MeshAnnouncer.EnsureStarted</c> flips <c>_started</c> to 1 before deriving the
/// descriptor. The null-descriptor path already resets it and retries on the next invocation - the
/// residual gap this locks in is a <em>thrown</em> descriptor derivation: before the fix, the
/// exception propagated out of <c>EnsureStarted</c> (failing whatever invocation triggered the lazy
/// start) and left <c>_started</c> stuck at 1 forever, permanently disabling the announcer. Per the
/// class's own documented contract (spec §6), every failure here must be swallowed and retried on
/// the next invocation, and nothing here may ever fail an invocation.
/// </summary>
public class MeshAnnouncerTest
{
    [Fact]
    public async Task EnsureStarted_WhenDescriptorDerivationThrows_SwallowsAndRetriesOnNextInvocation()
    {
        var info = new MeshServiceInfo("orders");
        var report = CloudServiceProfileReport.Evaluate(new CloudServiceBuilder("orders"), null);
        var descriptorSource = new CloudServiceDescriptorSource(info, report, handlerTypes: null);
        var resolver = new ThrowOnceResolver();
        var handler = new SignalingHandler();
        var http = new HttpClient(handler);
        await using var announcer = new MeshAnnouncer(
            info,
            descriptorSource,
            "http://collector.invalid/benzene/invoke",
            Array.Empty<IHealthCheck>(),
            http,
            TimeSpan.FromSeconds(5));

        // First invocation: the registry lookup throws deriving the descriptor. Before the fix this
        // propagated straight out of EnsureStarted - failing the caller's invocation - and left
        // _started stuck at 1 forever.
        var thrown = Record.Exception(() => announcer.EnsureStarted(resolver));
        Assert.Null(thrown);

        // Nothing should have been registered yet: the descriptor was never successfully derived,
        // so no announce loop can have started.
        var tooEarly = await Task.WhenAny(handler.RequestReceived, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(handler.RequestReceived, tooEarly);

        // Second invocation retries; this time the lookup succeeds, so the announce loop should
        // start and register with the collector. Before the fix, _started was stuck at 1 from the
        // first (failed) call, so this call was a permanent no-op and the assertion below timed out.
        announcer.EnsureStarted(resolver);

        var registered = await Task.WhenAny(handler.RequestReceived, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(handler.RequestReceived, registered);
        Assert.Contains("benzene:mesh:register", await handler.RequestReceived);
    }

    private sealed class ThrowOnceResolver : IServiceResolver
    {
        private int _calls;

        public T GetService<T>() where T : class => throw new NotSupportedException();

        public T? TryGetService<T>() where T : class
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("registry unavailable (simulated)");
            }

            return null;
        }

        public IEnumerable<T> GetServices<T>() where T : class => Array.Empty<T>();

        public void Dispose() { }
    }

    private sealed class SignalingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<string> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> RequestReceived => _tcs.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _tcs.TrySetResult(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
