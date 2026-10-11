using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.MessageHandling;

/// <summary>
/// wire-contracts §7: <c>benzene:ping</c> is a reachability probe the router answers itself - <c>ok</c>,
/// no payload, intercepted by topic id alone (any version) - unless the app opts out.
/// </summary>
public class PingTest
{
    private static Task<IBenzeneMessageResponse> SendAsync(string topic, Action<MicrosoftBenzeneServiceContainer>? configure = null,
        Dictionary<string, string>? headers = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzeneMessage();
        configure?.Invoke(container);
        var builder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        builder.UseMessageHandlers(Array.Empty<Type>(),
            router => router.AddMessageHandler<EchoHandler, EchoRequest, EchoResponse>("mcp:echo"));
        var application = new BenzeneMessageApplication(builder.Build());

        return application.HandleAsync(new BenzeneMessageRequest
        {
            Topic = topic,
            Headers = headers ?? new Dictionary<string, string>(),
            Body = string.Empty,
        }, new MicrosoftServiceResolverFactory(services.BuildServiceProvider()));
    }

    [Fact]
    public async Task Ping_IsAnsweredOk_WithNoPayload()
    {
        var response = await SendAsync(BenzeneTopic.Ping);

        Assert.Equal(BenzeneResultStatus.Ok, response.StatusCode);
        Assert.True(response.IsSuccessful);
        Assert.True(string.IsNullOrEmpty(response.Body), $"expected no payload, got '{response.Body}'");
    }

    [Fact]
    public async Task Ping_WithAVersion_IsStillAnswered_VersionIsIgnored()
    {
        var response = await SendAsync(BenzeneTopic.Ping,
            headers: new Dictionary<string, string> { ["benzene-version"] = "v9" });

        Assert.Equal(BenzeneResultStatus.Ok, response.StatusCode);
    }

    [Fact]
    public async Task Ping_Disabled_RoutesLikeAnyOtherTopic()
    {
        var response = await SendAsync(BenzeneTopic.Ping, x => x.DisablePing());

        Assert.Equal(BenzeneResultStatus.NotFound, response.StatusCode);
        Assert.False(response.IsSuccessful);
    }

    [Fact]
    public async Task OtherTopics_AreUnaffected()
    {
        var response = await SendAsync("mcp:unknown");

        Assert.Equal(BenzeneResultStatus.NotFound, response.StatusCode);
    }
}
