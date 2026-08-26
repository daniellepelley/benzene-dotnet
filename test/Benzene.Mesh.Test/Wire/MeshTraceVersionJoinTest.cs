using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Mesh.Wire;

/// <summary>
/// Resurrects the round-10 reviewer's live probe for task #98 (work/archive/bug-fix-designs-round10-2026-08.md
/// WP-V): a message carrying the <c>benzene-version</c> header through a pipeline with
/// <see cref="Extensions.UseMeshTrace{TContext}"/> must export a <see cref="MeshTraceEvent.TopicVersion"/>
/// matching the header - before the fix it was always <c>null</c>, because <c>UseMeshTrace</c> reads
/// <c>IMessageGetter&lt;BenzeneMessageContext&gt;.GetTopic</c> directly (mesh.md §3) and, before
/// WP-V, nothing ever joined the message's version signal into that topic except
/// <see cref="MessageRouter{TContext}"/> - which discarded the join instead of caching it.
/// </summary>
public class MeshTraceVersionJoinTest
{
    private sealed class CaptureExporter : IMeshTraceExporter
    {
        public List<MeshTraceEvent> Events { get; } = new();

        public void Export(MeshTraceEvent traceEvent) => Events.Add(traceEvent);
    }

    [Message("order:create")]
    public class OrderCreateHandler : IMessageHandler<Void, Void>
    {
        public Task<IBenzeneResult<Void>> HandleAsync(Void request)
            => Task.FromResult(BenzeneResult.Ok(new Void()));
    }

    [Fact]
    public async Task UseMeshTrace_HeaderVersionedMessage_ExportsTheDeclaredTopicVersion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.UsingBenzene(x => x.AddBenzene().AddBenzeneMessage()
            .AddMessageHandlers(new[] { typeof(OrderCreateHandler) }));

        var container = new MicrosoftBenzeneServiceContainer(services);
        var exporter = new CaptureExporter();
        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipeline
            .UseMeshTrace(new MeshServiceInfo("probe-service"), exporter, new BenzeneMessageMeshStatusReader())
            .UseMessageHandlers(new[] { typeof(OrderCreateHandler) });

        var app = new BenzeneMessageApplication(pipeline.Build());
        var request = new BenzeneMessageRequest
        {
            Topic = "order:create",
            Headers = new Dictionary<string, string> { [MessageVersionHeaders.Default] = "v2" },
            Body = "{}"
        };

        await app.HandleAsync(request, new MicrosoftServiceResolverFactory(services));

        var traceEvent = Assert.Single(exporter.Events);
        Assert.Equal("order:create", traceEvent.Topic);
        Assert.Equal("v2", traceEvent.TopicVersion);
    }
}
