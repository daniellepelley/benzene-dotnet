using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mcp;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Mcp;

public class McpMessageDispatcherTest
{
    [Fact]
    public void HeaderNames_AreTheReservedBenzeneNames()
    {
        Assert.Equal("benzene-mcp-tool", McpMessageDispatcher.ToolHeader);
        Assert.Equal("benzene-mcp-session", McpMessageDispatcher.SessionHeader);
    }

    [Fact]
    public async Task SendAsync_CarriesTheToolSessionAndVersionHeaders()
    {
        IDictionary<string, string>? seen = null;
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzeneMessage();
        var builder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        builder.Use<BenzeneMessageContext>((context, next) =>
        {
            seen = context.BenzeneMessageRequest.Headers;
            return next();
        });
        builder.UseMessageHandlers(Array.Empty<Type>(),
            router => router.AddMessageHandler<EchoHandler, EchoRequest, EchoResponse>("mcp:echo"));
        var pipeline = builder.Build();
        var resolver = new MicrosoftServiceResolverFactory(services.BuildServiceProvider());

        var dispatcher = new McpMessageDispatcher(pipeline, resolver, sessionId: "abc123");
        await dispatcher.SendAsync("mcp:echo", new JsonObject { ["name"] = "world" }, "v1",
            new Dictionary<string, string> { [McpMessageDispatcher.ToolHeader] = "greet" });

        Assert.NotNull(seen);
        Assert.Equal("greet", seen!["benzene-mcp-tool"]);
        Assert.Equal("abc123", seen["benzene-mcp-session"]);
        Assert.Equal("v1", seen["benzene-version"]);
        Assert.False(seen.ContainsKey("mcp-tool"));
        Assert.False(seen.ContainsKey("mcp-session"));
    }
}
