using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Mcp;
using Benzene.Mcp.Hosting;
using Benzene.Test.Examples;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Xunit;

namespace Benzene.Test.Mcp;

/// <summary>
/// MCP over Streamable HTTP, mounted on an API Gateway pipeline through the real Lambda test host:
/// the session header, the 202 for a notification, a tool call reaching a handler, and the 405 for a
/// method this binding does not offer.
/// </summary>
public class McpHttpTest
{
    private static AwsLambdaBenzeneTestHost CreateHost(string? path = null)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseApiGateway(apiGateway => apiGateway
                    .UseMcp(mcp =>
                        {
                            if (path is not null) mcp.AtPath(path);
                            mcp.ServerInfo("test", "1.0")
                                .Tool<EchoRequest>("greet", "Greets somebody.", "mcp:echo");
                        },
                        messageApp => messageApp.UseMessageHandlers(Array.Empty<Type>(),
                            router => router.AddMessageHandler<EchoHandler, EchoRequest, EchoResponse>("mcp:echo")))
                    .UseMessageHandlers()
                )
            )
            .BuildHost();
    }

    private static APIGatewayProxyRequest Post(string body, string path = "/mcp", string? sessionId = null)
    {
        var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
        if (sessionId is not null) headers["Mcp-Session-Id"] = sessionId;
        return new APIGatewayProxyRequest { HttpMethod = "POST", Path = path, Body = body, Headers = headers };
    }

    private static string? Header(APIGatewayProxyResponse response, string name) =>
        response.Headers?.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? SessionOf(APIGatewayProxyResponse response) => Header(response, "Mcp-Session-Id");

    private const string Initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}";
    private const string Initialized = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";

    [Fact]
    public async Task Initialize_IssuesASessionId_AndLaterCallsEchoIt()
    {
        var host = CreateHost();

        var initialize = await host.SendApiGatewayAsync(Post(Initialize));

        Assert.Equal(200, initialize.StatusCode);
        Assert.Contains("application/json", Header(initialize, "content-type"));
        var session = SessionOf(initialize);
        Assert.NotNull(session);
        Assert.Equal(32, session!.Length);
        Assert.Contains("\"protocolVersion\":\"2025-06-18\"", initialize.Body);

        var list = await host.SendApiGatewayAsync(Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", sessionId: session));
        Assert.Equal(200, list.StatusCode);
        Assert.Equal(session, SessionOf(list));
        Assert.Contains("\"name\":\"greet\"", list.Body);

        // An id this server could not have issued is not echoed: the client is told, by its absence, that
        // it is not in a session this server knows.
        var stranger = await host.SendApiGatewayAsync(Post("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}", sessionId: "not-a-session"));
        Assert.Equal(200, stranger.StatusCode);
        Assert.Null(SessionOf(stranger));
    }

    [Fact]
    public async Task Notification_IsAcceptedWithNoBody()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(Post(Initialized));

        Assert.Equal(202, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(response.Body));
    }

    [Fact]
    public async Task ToolsCall_ReachesTheHandlerThroughThePipeline()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(Post(
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"greet\",\"arguments\":{\"name\":\"world\"}}}"));

        Assert.Equal(200, response.StatusCode);
        var json = (JsonObject)JsonNode.Parse(response.Body)!;
        Assert.False(json["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("hello world", json["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_OnThePath_IsMethodNotAllowed()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(new APIGatewayProxyRequest { HttpMethod = "GET", Path = "/mcp" });

        Assert.Equal(405, response.StatusCode);
        Assert.Equal("POST", Header(response, "Allow"));
    }

    [Fact]
    public async Task ThePathIsConfigurable()
    {
        var host = CreateHost("/ai/tools/");

        var response = await host.SendApiGatewayAsync(Post("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"}", path: "/ai/tools"));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"result\":{}", response.Body);
    }
}
