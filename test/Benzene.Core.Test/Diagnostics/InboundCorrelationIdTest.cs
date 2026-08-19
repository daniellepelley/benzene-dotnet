using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Clients.CorrelationId;
using Benzene.Core.Middleware;
using Benzene.Diagnostics.Correlation;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Diagnostics;

/// <summary>
/// Covers the inbound <c>UseCorrelationId()</c> - the counterpart of <c>Benzene.Clients</c>'s outbound
/// one. Without it a consumer's <see cref="ICorrelationId"/> is a fresh GUID and the caller's chain
/// breaks at the first hop.
/// </summary>
public class InboundCorrelationIdTest
{
    /// <summary>A stand-in transport context: a bag of headers, nothing else.</summary>
    private class FakeContext
    {
        public FakeContext(IDictionary<string, string> headers) => Headers = headers;
        public IDictionary<string, string> Headers { get; }
    }

    private class FakeHeadersGetter : IMessageHeadersGetter<FakeContext>
    {
        public IDictionary<string, string> GetHeaders(FakeContext context) => context.Headers;
    }

    private static (IMiddlewarePipeline<FakeContext> Pipeline, IServiceResolver Resolver) Pipeline(
        Action<IMiddlewarePipelineBuilder<FakeContext>> configure, bool registerHeadersGetter = true)
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddCorrelationId();
        if (registerHeadersGetter)
        {
            container.AddScoped<IMessageHeadersGetter<FakeContext>, FakeHeadersGetter>();
        }

        var builder = new MiddlewarePipelineBuilder<FakeContext>(container);
        configure(builder);
        var pipeline = builder.Build();

        return (pipeline, new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()));
    }

    [Fact]
    public async Task UseCorrelationId_ReadsTheDefaultHeader_AndSeedsICorrelationId()
    {
        var (pipeline, resolver) = Pipeline(app => app.UseCorrelationId());

        await pipeline.HandleAsync(new FakeContext(new Dictionary<string, string>
        {
            { CorrelationHeaderDefaults.HeaderKey, "from-the-caller" }
        }), resolver);

        Assert.Equal("from-the-caller", resolver.GetService<ICorrelationId>().Get());
    }

    [Fact]
    public async Task UseCorrelationId_JoinsUpWithTheOutboundStampingMiddleware_WithNoConfiguration()
    {
        // The whole point: a service stamps x-correlation-id on the way out (Benzene.Clients'
        // UseCorrelationId), and the next service reads the same key back off the wire by default.
        var outboundContext = new OutboundContext("some-topic", "some-message");
        await new CorrelationIdMiddleware(new StubCorrelationId("chain-id")).HandleAsync(outboundContext, () => Task.CompletedTask);

        var (pipeline, resolver) = Pipeline(app => app.UseCorrelationId());
        await pipeline.HandleAsync(new FakeContext(outboundContext.Headers), resolver);

        Assert.Equal("chain-id", resolver.GetService<ICorrelationId>().Get());
    }

    [Fact]
    public async Task UseCorrelationId_MatchesTheHeaderCaseInsensitively()
    {
        var (pipeline, resolver) = Pipeline(app => app.UseCorrelationId());

        await pipeline.HandleAsync(new FakeContext(new Dictionary<string, string> { { "X-Correlation-Id", "shouty" } }), resolver);

        Assert.Equal("shouty", resolver.GetService<ICorrelationId>().Get());
    }

    [Fact]
    public async Task UseCorrelationId_WithAnExplicitKey_ReadsThatHeader()
    {
        var (pipeline, resolver) = Pipeline(app => app.UseCorrelationId("x-my-correlation"));

        await pipeline.HandleAsync(new FakeContext(new Dictionary<string, string> { { "x-my-correlation", "custom" } }), resolver);

        Assert.Equal("custom", resolver.GetService<ICorrelationId>().Get());
    }

    [Fact]
    public async Task UseCorrelationId_NoInboundHeader_LeavesTheSelfGeneratedIdInPlace()
    {
        var (pipeline, resolver) = Pipeline(app => app.UseCorrelationId());

        await pipeline.HandleAsync(new FakeContext(new Dictionary<string, string>()), resolver);

        Assert.True(Guid.TryParse(resolver.GetService<ICorrelationId>().Get(), out _));
    }

    [Fact]
    public void UseCorrelationId_WithNoHeadersGetterRegistered_FailsWhenThePipelineIsResolved_NotOnTheMessagePath()
    {
        // Rule 3: the price of the convention is that a misconfigured pipeline is named before a
        // message is handled. The middleware resolves IMessageHeadersGetter<TContext> at construction,
        // so the failure lands here rather than as a null dereference mid-message.
        var (pipeline, resolver) = Pipeline(app => app.UseCorrelationId(), registerHeadersGetter: false);

        Assert.ThrowsAny<Exception>(() => pipeline.HandleAsync(new FakeContext(new Dictionary<string, string>()), resolver).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task OutboundUseCorrelationId_StillResolvesToTheClientsOverload_WhenBothNamespacesAreImported()
    {
        // Both this file's usings are in scope; the non-generic OutboundContext overload must still win
        // on an outbound pipeline, or adding the inbound one would have been a breaking change.
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddCorrelationId();
        var builder = new MiddlewarePipelineBuilder<OutboundContext>(container);
        builder.UseCorrelationId();
        var pipeline = builder.Build();

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var context = new OutboundContext("some-topic", "some-message");
        await pipeline.HandleAsync(context, resolver);

        Assert.True(context.Headers.ContainsKey(CorrelationHeaderDefaults.HeaderKey));
    }

    private class StubCorrelationId : ICorrelationId
    {
        private string _value;
        public StubCorrelationId(string value) => _value = value;
        public void Set(string correlationId) => _value = correlationId;
        public string Get() => _value;
    }
}
