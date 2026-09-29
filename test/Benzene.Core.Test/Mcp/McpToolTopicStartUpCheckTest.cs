using System;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mcp;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Mcp;

/// <summary>A topic-bound tool whose topic nobody handles is a start-up error naming the tool, not a not-found on every call.</summary>
public class McpToolTopicStartUpCheckTest
{
    private static MicrosoftServiceResolverAdapter Resolver(params McpToolDefinition[] tools)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzeneMessage();
        container.AddSingleton(new McpToolCatalog(tools));
        new MiddlewarePipelineBuilder<BenzeneMessageContext>(container)
            .UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<EchoHandler, EchoRequest, EchoResponse>("mcp:echo"));
        return new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
    }

    [Fact]
    public void Passes_WhenEveryTopicBoundToolHasAHandler()
    {
        var resolver = Resolver(
            McpToolDefinition.ForTopic("greet", "Greets.", McpSchema.Object(), "mcp:echo"),
            McpToolDefinition.Custom("custom", "Custom.", McpSchema.Object(), (_, _, _) => throw new NotSupportedException()));

        new McpToolTopicStartUpCheck().Check(resolver);
    }

    [Fact]
    public void Throws_NamingTheToolAndTopic_WhenNoHandlerAnswersIt()
    {
        var resolver = Resolver(
            McpToolDefinition.ForTopic("greet", "Greets.", McpSchema.Object(), "mcp:echo"),
            McpToolDefinition.ForTopic("orphan", "Nobody answers.", McpSchema.Object(), "mcp:nobody"));

        var ex = Assert.Throws<McpToolWithoutHandlerException>(() => new McpToolTopicStartUpCheck().Check(resolver));

        var missing = Assert.Single(ex.Missing);
        Assert.Equal("orphan", missing.Tool);
        Assert.Equal("mcp:nobody", missing.Topic);
        Assert.Contains("tool 'orphan' -> topic 'mcp:nobody'", ex.Message);
        Assert.Contains("UseMcp", ex.Message);
    }
}
