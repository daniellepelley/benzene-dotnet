using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Top-level DI registration for the in-process message dispatcher: a <c>BenzeneMessage</c> pipeline
/// invoked directly, in the same runtime, rather than through any wire transport.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Builds an in-process <c>BenzeneMessage</c> pipeline and registers it as the target for
    /// <see cref="InProcessClientMiddleware"/>, so an outbound route's <c>.UseInProcess()</c> can
    /// dispatch straight to it without leaving the process.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="configure">
    /// Configures the in-process pipeline - typically at least <c>.UseMessageHandlers(...)</c>, plus
    /// whatever cross-cutting middleware (logging, validation) the dispatched topics need. This
    /// pipeline is independent of any other <c>BenzeneMessage</c> pipeline the service may also expose
    /// over HTTP or Lambda (see <c>UseBenzeneMessage</c>) - register handlers on both if a topic must
    /// be reachable both ways.
    /// </param>
    /// <returns>The same container, for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddInProcessMessaging(pipeline =&gt; pipeline.UseMessageHandlers(handlers =&gt; handlers
    ///     .Add&lt;OrderCreatedHandler&gt;()));
    ///
    /// services.AddOutboundRouting(routing =&gt; routing
    ///     .Route("order:created", pipeline =&gt; pipeline.UseInProcess()));
    /// </code>
    /// </example>
    public static IBenzeneServiceContainer AddInProcessMessaging(this IBenzeneServiceContainer services, Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> configure)
    {
        // Reuses the same BenzeneMessage request/response plumbing UseBenzeneMessage(HTTP/Lambda) relies
        // on, but deliberately skips AddBenzeneMessage()'s ITransportInfo("benzene") registration: a
        // service that only dispatches in-process never exposes that wire endpoint, and advertising it
        // anyway would misrepresent the service's transport surface to the mesh/descriptor.
        services.AddBenzeneMessageHandling();

        var builder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(services);
        configure(builder);
        var pipeline = builder.Build();
        var taggedPipeline = new TransportMiddlewarePipeline<BenzeneMessageContext>(TransportNames.InProcess, pipeline);

        var dispatcher = new MiddlewareApplication<IBenzeneMessageRequest, BenzeneMessageContext, IBenzeneMessageResponse>(
            taggedPipeline,
            @event => new BenzeneMessageContext(@event),
            context => context.BenzeneMessageResponse);

        services.AddSingleton<IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>>(dispatcher);
        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.InProcess));

        return services;
    }
}
