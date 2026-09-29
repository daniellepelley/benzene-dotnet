using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core.MessageHandlers;
using Benzene.Mcp;
using Benzene.Mcp.Hosting;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Mcp;

/// <summary>
/// MCP over stdio through the self-hosted worker: one frame per line in, one per line out, nothing
/// for a notification, and the loop survives a message it could not answer.
/// </summary>
public class McpStdioWorkerTest
{
    [Fact]
    public async Task AnswersEachLine_SkipsNotifications_AndStopsAtEndOfInput()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var startup = new BenzeneWorkerBuilder(container);

        var input = new StringReader(string.Join("\n",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"greet\",\"arguments\":{\"name\":\"stdio\"}}}",
            "not json at all",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}"));
        var output = new StringWriter();

        startup.UseMcpStdio(
            mcp => mcp.ServerInfo("stdio-test", "1.0").Tool<EchoRequest>("greet", "Greets.", "mcp:echo"),
            pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<EchoHandler, EchoRequest, EchoResponse>("mcp:echo")),
            input, output);

        var worker = startup.Create(new MicrosoftServiceResolverFactory(services.BuildServiceProvider()));
        await worker.StartAsync(CancellationToken.None);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(4, lines.Length);

        var initialize = (JsonObject)JsonNode.Parse(lines[0])!;
        Assert.Equal("stdio-test", initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var call = (JsonObject)JsonNode.Parse(lines[1])!;
        Assert.Contains("hello stdio", call["result"]!["content"]![0]!["text"]!.GetValue<string>());

        var parseError = (JsonObject)JsonNode.Parse(lines[2])!;
        Assert.Equal(-32700, parseError["error"]!["code"]!.GetValue<int>());

        var ping = (JsonObject)JsonNode.Parse(lines[3])!;
        Assert.Equal(3, ping["id"]!.GetValue<int>());
    }
}
