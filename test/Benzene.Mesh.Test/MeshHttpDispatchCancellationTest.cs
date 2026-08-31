using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Http;
using Benzene.Http.BenzeneMessage;
using Benzene.Mesh.Collector;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Round-17 composition finding (#285): <see cref="BenzeneMessageHttpMiddleware{TContext}"/> called
/// the 2-argument <c>HandleAsync(request, factory)</c> overload, which hardcodes
/// <see cref="CancellationToken.None"/> into the INNER DI scope it creates for the dispatched
/// envelope — so the real HTTP request's cancellation, already seeded onto the OUTER scope's
/// <see cref="ICancellationTokenAccessor"/> by the "SeedCancellationToken" pipeline middleware
/// (<c>BenzeneExtensions.BuildHttpPipeline</c>), never reached <c>FleetQueryMessageHandler</c>/
/// <c>MeshDispatchMessageHandler</c> despite #250/#185 correctly resolving the accessor "at the
/// point of use". This made both of those fixes inert on every host built on
/// <c>Benzene.Http</c>'s <c>UseBenzeneMessage</c> HTTP-envelope pattern, including the shipped
/// <c>deploy/Mesh/Benzene.Mesh.Host</c>.
/// </summary>
/// <remarks>
/// Unlike the round-16 #250 regression test (<see cref="MeshCollectorQueryCancellationTest"/>), which
/// hand-shares a single <see cref="CancellationTokenAccessor"/> instance directly between the
/// handler and the timeout middleware and so cannot detect a transport that fails to carry the token
/// into a freshly-created inner scope, this test goes through REAL DI scope creation end to end:
/// a real <see cref="MicrosoftBenzeneServiceContainer"/>, a real <see cref="IServiceResolverFactory"/>,
/// an OUTER scope seeded exactly as the real HTTP pipeline seeds it, and the actual
/// <see cref="BenzeneMessageHttpMiddleware{TContext}"/> dispatching through its real
/// <c>DispatchAsync</c> — proving the INNER scope the handler resolves its accessor from is the one
/// actually seeded with the outer request's token, not a fresh, dead one.
/// </remarks>
public class MeshHttpDispatchCancellationTest
{
    // Public (not private) because Moq needs to build a dynamic proxy for the adapter interfaces
    // closed over this type, which requires the type argument to be accessible.
    public class FakeHttpContext : IHttpContext
    {
    }

    private sealed class ObservingReadModel : IMeshFleetReadModel
    {
        public CancellationToken? Observed { get; private set; }

        public Task<FleetView> FleetAsync(MeshTimeRange? range = null, CancellationToken cancellationToken = default)
        {
            Observed = cancellationToken;
            return Task.FromResult(new FleetView());
        }

        public Task<ServiceView?> ServiceAsync(string name, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceView?>(null);

        public Task<TopicSummary?> TopicAsync(string id, string? version, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => Task.FromResult<TopicSummary?>(null);

        public Task<TraceView?> TraceAsync(string traceId, CancellationToken cancellationToken = default)
            => Task.FromResult<TraceView?>(null);

        public Task<CorrelationView?> CorrelationAsync(string correlationId, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CorrelationView?>(null);
    }

    private const string FleetQueryEnvelope = "{\"topic\":\"benzene:mesh:query:fleet\",\"headers\":{},\"body\":\"{}\"}";

    [Fact]
    public async Task DispatchAsync_ThroughRealScopeCreation_SeedsTheInnerScopeWithTheOuterRequestsCancellationToken()
    {
        // Real container + real pipeline, exactly as deploy/Mesh/Benzene.Mesh.Host wires mesh:query:*.
        var services = new ServiceCollection();
        services.AddLogging();
        var readModel = new ObservingReadModel();
        services.AddSingleton<IMeshFleetReadModel>(readModel);

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineBuilder.UseMessageHandlers(MeshCollectorHandlers.Queries);
        var pipeline = pipelineBuilder.Build();

        var rootFactory = container.CreateServiceResolverFactory();

        // Simulate the real OUTER per-HTTP-request DI scope, seeded exactly as
        // BenzeneExtensions.BuildHttpPipeline's "SeedCancellationToken" middleware seeds it from a
        // genuinely cancelled HttpContext.RequestAborted (e.g. a disconnected browser tab).
        using var outerRequestScope = rootFactory.CreateScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        outerRequestScope.SeedCancellationToken(cts.Token);

        // The real middleware, constructed exactly as Benzene.Http's DI wiring constructs it, with
        // the OUTER scope as its "current invocation scope" service resolver — precisely the field
        // DispatchAsync reads to resolve IServiceResolverFactory (and, per the fix, ICancellationTokenAccessor).
        var requestAdapterMock = new Mock<IHttpRequestAdapter<FakeHttpContext>>();
        requestAdapterMock.Setup(x => x.Map(It.IsAny<FakeHttpContext>()))
            .Returns(new HttpRequest { Method = "POST", Path = "/benzene-message" });

        var bodyGetterMock = new Mock<IMessageBodyGetter<FakeHttpContext>>();
        bodyGetterMock.Setup(x => x.GetBody(It.IsAny<FakeHttpContext>())).Returns(FleetQueryEnvelope);

        var responseAdapterMock = new Mock<IBenzeneResponseAdapter<FakeHttpContext>>();

        var middleware = new BenzeneMessageHttpMiddleware<FakeHttpContext>(
            new BenzeneMessageHttpOptions(),
            pipeline,
            outerRequestScope,
            requestAdapterMock.Object,
            bodyGetterMock.Object,
            responseAdapterMock.Object,
            new DefaultHttpStatusCodeMapper());

        await middleware.HandleAsync(new FakeHttpContext(), () => Task.CompletedTask);

        // Green: the INNER scope the query handler actually ran in — a scope distinct from
        // outerRequestScope, created fresh by the 3-argument HandleAsync overload's
        // serviceResolverFactory.CreateScope() — was nonetheless seeded with the outer request's real
        // (cancelled) token. Before the fix, DispatchAsync called the 2-argument overload, which
        // hardcodes CancellationToken.None into that inner scope regardless of what the outer scope's
        // accessor holds, so this would observe IsCancellationRequested == false.
        Assert.NotNull(readModel.Observed);
        Assert.True(readModel.Observed!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task DispatchAsync_WithNoCancellationSeeded_StillDispatchesNormally()
    {
        // No SeedCancellationToken call on the outer scope at all — the accessor stays at its default
        // CancellationToken.None, mirroring a transport/pipeline with no cancellation signal. The fix
        // must not regress this: TryGetService resolves the accessor (present via AddBenzene()) with
        // its default token, and dispatch proceeds exactly as before.
        var services = new ServiceCollection();
        services.AddLogging();
        var readModel = new ObservingReadModel();
        services.AddSingleton<IMeshFleetReadModel>(readModel);

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineBuilder.UseMessageHandlers(MeshCollectorHandlers.Queries);
        var pipeline = pipelineBuilder.Build();

        var rootFactory = container.CreateServiceResolverFactory();
        using var outerRequestScope = rootFactory.CreateScope();

        var requestAdapterMock = new Mock<IHttpRequestAdapter<FakeHttpContext>>();
        requestAdapterMock.Setup(x => x.Map(It.IsAny<FakeHttpContext>()))
            .Returns(new HttpRequest { Method = "POST", Path = "/benzene-message" });

        var bodyGetterMock = new Mock<IMessageBodyGetter<FakeHttpContext>>();
        bodyGetterMock.Setup(x => x.GetBody(It.IsAny<FakeHttpContext>())).Returns(FleetQueryEnvelope);

        var bodiesWritten = new List<string>();
        var responseAdapterMock = new Mock<IBenzeneResponseAdapter<FakeHttpContext>>();
        responseAdapterMock.Setup(x => x.SetBody(It.IsAny<FakeHttpContext>(), It.IsAny<string>()))
            .Callback<FakeHttpContext, string>((_, body) => bodiesWritten.Add(body));

        var middleware = new BenzeneMessageHttpMiddleware<FakeHttpContext>(
            new BenzeneMessageHttpOptions(),
            pipeline,
            outerRequestScope,
            requestAdapterMock.Object,
            bodyGetterMock.Object,
            responseAdapterMock.Object,
            new DefaultHttpStatusCodeMapper());

        await middleware.HandleAsync(new FakeHttpContext(), () => Task.CompletedTask);

        Assert.NotNull(readModel.Observed);
        Assert.False(readModel.Observed!.Value.IsCancellationRequested);
        var body = Assert.Single(bodiesWritten);
        Assert.Contains("\"statusCode\":\"ok\"", body);
    }
}
