using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Http;

namespace Benzene.Mcp.Hosting;

/// <summary>
/// Adds an MCP (Streamable HTTP) endpoint to any Benzene HTTP pipeline, the same way
/// <c>UseBenzeneMessage</c> adds the raw envelope endpoint: the tools dispatch into an inner
/// BenzeneMessage pipeline configured inline, so middleware, validation, mesh feeds and start-up
/// checks all see a tool call exactly as they see the same topic over HTTP.
/// </summary>
public static class McpHttpExtensions
{
    /// <summary>
    /// Adds an MCP endpoint (default path <c>/mcp</c>), configuring the tools and the inner
    /// BenzeneMessage pipeline inline.
    /// </summary>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The HTTP pipeline builder to add the endpoint to.</param>
    /// <param name="configure">Configures the tools, server info and path.</param>
    /// <param name="pipeline">Configures the inner BenzeneMessage pipeline the tools dispatch into (typically <c>p =&gt; p.UseMessageHandlers()</c>).</param>
    /// <returns>The pipeline builder for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseMcp<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app,
        Action<McpBuilder> configure,
        Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> pipeline)
        where TContext : IHttpContext
    {
        return app.UseMcp(configure, app.CreateMiddlewarePipeline(pipeline));
    }

    /// <summary>
    /// Adds an MCP endpoint using an already-built inner BenzeneMessage pipeline (for when the same
    /// pipeline is shared with the direct invoke path or the BenzeneMessage HTTP endpoint).
    /// </summary>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The HTTP pipeline builder to add the endpoint to.</param>
    /// <param name="configure">Configures the tools, server info and path.</param>
    /// <param name="pipeline">The built BenzeneMessage pipeline the tools dispatch into.</param>
    /// <returns>The pipeline builder for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseMcp<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app,
        Action<McpBuilder> configure,
        IMiddlewarePipeline<BenzeneMessageContext> pipeline)
        where TContext : IHttpContext
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

        return app.Use(resolver => new McpHttpMiddleware<TContext>(
            options,
            pipeline,
            resolver,
            resolver.GetService<IHttpRequestAdapter<TContext>>(),
            resolver.GetService<IMessageBodyGetter<TContext>>(),
            resolver.GetService<IBenzeneResponseAdapter<TContext>>()));
    }
}
