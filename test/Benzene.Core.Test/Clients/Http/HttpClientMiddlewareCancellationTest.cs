using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Clients.Http;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Clients.Http;

/// <summary>
/// #270: the given-instance <c>UseHttpClient(httpClient)</c> overload used to construct
/// <see cref="HttpClientMiddleware"/> via its no-accessor constructor, silently dropping cancellation
/// forwarding on that path even though it is a documented first-class way to configure the send
/// pipeline (unlike the DI-resolved sibling overload, which already picked up
/// <see cref="ICancellationTokenAccessor"/> via constructor injection). Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - a capturing
/// <see cref="HttpClient"/>, not <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class HttpClientMiddlewareCancellationTest
{
    // Captured at HttpClient.SendAsync itself, which is the call the middleware makes. An HttpMessageHandler
    // underneath never sees the caller's token: HttpClient links it with its own timeout source first, so
    // a handler-side capture can equal neither the forwarded token nor CancellationToken.None.
    private sealed class CapturingHttpClient : HttpClient
    {
        public CancellationToken? ObservedToken { get; private set; }

        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    [Fact]
    public async Task UseHttpClient_GivenInstance_ForwardsTheAmbientTokenToSendAsync()
    {
        using var cts = new CancellationTokenSource();
        var httpClient = new CapturingHttpClient();

        var pipeline = new MiddlewarePipelineBuilder<HttpSendMessageContext>(new NullBenzeneServiceContainer())
            .UseHttpClient(httpClient)
            .Build();

        var context = new HttpSendMessageContext(new HttpRequestMessage(HttpMethod.Get, "https://example.test/"));
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        Assert.Equal(cts.Token, httpClient.ObservedToken);
    }

    [Fact]
    public async Task UseHttpClient_GivenInstance_WithNoAccessorRegistered_SendsWithNoneToken()
    {
        var httpClient = new CapturingHttpClient();

        var pipeline = new MiddlewarePipelineBuilder<HttpSendMessageContext>(new NullBenzeneServiceContainer())
            .UseHttpClient(httpClient)
            .Build();

        var context = new HttpSendMessageContext(new HttpRequestMessage(HttpMethod.Get, "https://example.test/"));
        await pipeline.HandleAsync(context, new NullServiceResolver());

        Assert.Equal(CancellationToken.None, httpClient.ObservedToken);
    }
}
