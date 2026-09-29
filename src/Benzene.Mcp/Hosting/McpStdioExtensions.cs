using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.SelfHost;

namespace Benzene.Mcp.Hosting;

/// <summary>Adds an MCP stdio server to the self-hosted worker.</summary>
public static class McpStdioExtensions
{
    /// <summary>
    /// Adds an MCP server that reads JSON-RPC messages one per line from <paramref name="input"/>
    /// (standard input by default) and writes answers to <paramref name="output"/> (standard output),
    /// dispatching tools into the inner BenzeneMessage pipeline configured inline.
    /// </summary>
    /// <param name="app">The worker startup to add the server to.</param>
    /// <param name="configure">Configures the tools and server info.</param>
    /// <param name="pipeline">Configures the inner BenzeneMessage pipeline the tools dispatch into.</param>
    /// <param name="input">Where messages arrive; standard input when <c>null</c>.</param>
    /// <param name="output">Where answers go; standard output when <c>null</c>. Nothing else in the process may write to it.</param>
    /// <returns>The worker startup for method chaining.</returns>
    public static IBenzeneWorkerStartup UseMcpStdio(this IBenzeneWorkerStartup app,
        Action<McpBuilder> configure,
        Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> pipeline,
        TextReader? input = null,
        TextWriter? output = null)
    {
        var builder = new McpBuilder();
        configure(builder);
        var options = builder.Build();

        app.Register(x =>
        {
            x.AddBenzeneMessage();
            x.AddSingleton(new McpToolCatalog(options.Tools));
            x.TryAddSingletonImplementation<IStartUpCheck, McpToolTopicStartUpCheck>();
        });

        var pipelineBuilder = app.Create<BenzeneMessageContext>();
        pipeline(pipelineBuilder);
        var built = pipelineBuilder.Build();

        var log = options.Log ?? (message => Console.Error.WriteLine($"{options.ServerInfo.Name}-mcp: {message}"));
        var server = new McpServer(options, built, log);

        app.Add(serviceResolverFactory => new McpStdioWorker(server, serviceResolverFactory,
            input ?? Console.In, output ?? Console.Out, log));
        return app;
    }
}
