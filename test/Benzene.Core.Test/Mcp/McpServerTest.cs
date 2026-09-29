using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Benzene.Mcp;
using Xunit;

namespace Benzene.Test.Mcp;

/// <summary>
/// The protocol on its own: initialize, the tool list, and tools/call dispatched through the real
/// BenzeneMessage pipeline (a topic-bound tool) and through a body the app wrote (a custom tool).
/// </summary>
public class McpServerTest
{
    private sealed class Refusal : Exception
    {
        public Refusal(string message) : base(message) { }
    }

    /// <summary>A server plus the request scope it answers in, so a test never shares state with another.</summary>
    private sealed class Harness
    {
        public Harness(McpServer server, Benzene.Abstractions.DI.IServiceResolver resolver)
        {
            Server = server;
            Resolver = resolver;
        }

        public McpServer Server { get; }
        public Benzene.Abstractions.DI.IServiceResolver Resolver { get; }

        public async Task<JsonObject?> SendAsync(string method, JsonObject? parameters = null, bool notification = false, string? sessionId = null)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (!notification) request["id"] = 1;
            if (parameters is not null) request["params"] = parameters;
            return await Server.HandleAsync(request.ToJsonString(), Resolver, sessionId);
        }
    }

    private static Harness CreateServer(Action<McpBuilder>? extra = null, List<string>? log = null)
    {
        var (resolver, pipeline) = McpTestFixtures.EchoPipeline();
        var builder = new McpBuilder()
            .ServerInfo("test-server", "1.2.3")
            .Instructions("Say hello first.")
            .Tool<EchoRequest>("greet", "Greets somebody by name.", "mcp:echo")
            .Tool("whoami", "Tells you the session.", McpSchema.Object(),
                (call, _, _) => Task.FromResult(McpToolResult.Ok($"session:{call.SessionId ?? "none"}")))
            .Tool("ask_twice", "Asks the pipeline twice from a custom body.", McpSchema.Object(("name", McpSchema.Text("A name."), true)),
                async (call, context, ct) =>
                {
                    var name = McpArgs.RequiredText(call.Arguments, "name");
                    var first = await context.Dispatcher.AskAsync("mcp:echo", new JsonObject { ["name"] = name }, cancellationToken: ct);
                    var second = await context.Dispatcher.AskAsync("mcp:echo", new JsonObject { ["name"] = name + "!" }, cancellationToken: ct);
                    return McpToolResult.Ok(first.Text + " / " + second.Text);
                })
            .Tool("explode", "Throws.", McpSchema.Object(), (_, _, _) => throw new InvalidOperationException("kaboom"), writes: true)
            .Tool("refuse", "Refuses in the app's own words.", McpSchema.Object(), (_, _, _) => throw new Refusal("That year is locked."))
            .OnToolFailure(e => e is Refusal ? McpToolResult.Refused(e.Message) : null)
            .Log(message => log?.Add(message));
        extra?.Invoke(builder);
        return new Harness(new McpServer(builder.Build(), pipeline), resolver);
    }

    private static string ToolText(JsonObject? response) =>
        response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool ToolIsError(JsonObject? response) =>
        response!["result"]!["isError"]!.GetValue<bool>();

    [Fact]
    public async Task Initialize_NegotiatesTheVersionAndDescribesTheServer()
    {
        var server = CreateServer();

        var response = await server.SendAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });

        var result = response!["result"]!;
        Assert.Equal("2025-03-26", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("test-server", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("1.2.3", result["serverInfo"]!["version"]!.GetValue<string>());
        Assert.Equal("Say hello first.", result["instructions"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task Initialize_UnknownVersion_AnswersWithTheNewestSpoken()
    {
        var server = CreateServer();

        var response = await server.SendAsync("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });

        Assert.Equal(McpServer.ProtocolVersions[0], response!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolsList_AdvertisesEveryToolWithItsSchemaAndAnnotations()
    {
        var server = CreateServer();

        var response = await server.SendAsync("tools/list");

        var tools = (JsonArray)response!["result"]!["tools"]!;
        Assert.Equal(5, tools.Count);
        var greet = (JsonObject)tools[0]!;
        Assert.Equal("greet", greet["name"]!.GetValue<string>());
        Assert.Equal("object", greet["inputSchema"]!["type"]!.GetValue<string>());
        Assert.Equal("string", greet["inputSchema"]!["properties"]!["name"]!["type"]!.GetValue<string>());
        Assert.True(greet["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.True(greet["annotations"]!["idempotentHint"]!.GetValue<bool>());
        var explode = (JsonObject)tools[3]!;
        Assert.False(explode["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(explode["annotations"]!["destructiveHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ToolsCall_TopicBound_DispatchesThroughThePipelineAndReturnsTheHandlerBody()
    {
        var server = CreateServer();

        var response = await server.SendAsync("tools/call",
            new JsonObject { ["name"] = "greet", ["arguments"] = new JsonObject { ["name"] = "world" } });

        Assert.False(ToolIsError(response));
        Assert.Contains("hello world", ToolText(response));
    }

    [Fact]
    public async Task ToolsCall_TopicBound_HandlerFailure_IsARefusalTheModelCanRead()
    {
        var server = CreateServer();

        var response = await server.SendAsync("tools/call",
            new JsonObject { ["name"] = "greet", ["arguments"] = new JsonObject { ["name"] = "nobody" } });

        Assert.True(ToolIsError(response));
        Assert.StartsWith("not-found:", ToolText(response));
        Assert.Contains("There is nobody by that name.", ToolText(response));
        Assert.Null(response!["error"]);
    }

    [Fact]
    public async Task ToolsCall_Custom_ReceivesTheSessionAndCanAskThePipeline()
    {
        var server = CreateServer();

        var whoami = await server.SendAsync("tools/call", new JsonObject { ["name"] = "whoami" }, sessionId: "abc123");
        Assert.Equal("session:abc123", ToolText(whoami));

        var twice = await server.SendAsync("tools/call",
            new JsonObject { ["name"] = "ask_twice", ["arguments"] = new JsonObject { ["name"] = "bob" } });
        Assert.False(ToolIsError(twice));
        Assert.Contains("hello bob", ToolText(twice));
        Assert.Contains("hello bob!", ToolText(twice));
    }

    [Fact]
    public async Task ToolsCall_MissingArgument_IsARefusedToolResultNotAProtocolError()
    {
        var server = CreateServer();

        var response = await server.SendAsync("tools/call", new JsonObject { ["name"] = "ask_twice" });

        Assert.Null(response!["error"]);
        Assert.True(ToolIsError(response));
        Assert.Equal("'name' is required.", ToolText(response));
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsARefusedToolResult()
    {
        var server = CreateServer();

        var response = await server.SendAsync("tools/call", new JsonObject { ["name"] = "nope" });

        Assert.True(ToolIsError(response));
        Assert.Contains("no tool called 'nope'", ToolText(response));
    }

    [Fact]
    public async Task ToolsCall_MappedDomainException_IsARefusal_UnmappedIsAnInternalErrorWithAReferenceOnly()
    {
        var log = new List<string>();
        var server = CreateServer(log: log);

        var refused = await server.SendAsync("tools/call", new JsonObject { ["name"] = "refuse" });
        Assert.True(ToolIsError(refused));
        Assert.Equal("That year is locked.", ToolText(refused));

        var exploded = await server.SendAsync("tools/call", new JsonObject { ["name"] = "explode" });
        Assert.Null(exploded!["result"]);
        Assert.Equal(-32603, exploded["error"]!["code"]!.GetValue<int>());
        var message = exploded["error"]!["message"]!.GetValue<string>();
        Assert.DoesNotContain("kaboom", message);
        var logged = Assert.Single(log);
        Assert.Contains("kaboom", logged);
        var reference = logged.Substring(0, 8);
        Assert.Contains(reference, message);
    }

    [Fact]
    public async Task Notification_IsNotAnswered_PingIsEmpty_UnknownMethodAndBadJsonAreProtocolErrors()
    {
        var server = CreateServer();

        Assert.Null(await server.SendAsync("notifications/initialized", notification: true));

        var ping = await server.SendAsync("ping");
        Assert.Empty((JsonObject)ping!["result"]!);

        var unknown = await server.SendAsync("resources/list");
        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());

        var bad = await server.Server.HandleAsync("{not json", server.Resolver);
        Assert.Equal(-32700, bad!["error"]!["code"]!.GetValue<int>());

        var noMethod = await server.Server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":7}", server.Resolver);
        Assert.Equal(-32600, noMethod!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(7, noMethod["id"]!.GetValue<int>());
    }
}
