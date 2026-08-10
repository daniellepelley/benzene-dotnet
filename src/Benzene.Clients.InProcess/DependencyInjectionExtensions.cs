using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Top-level DI registration for in-process message dispatch: one or more <c>BenzeneMessage</c>
/// pipelines invoked directly, in the same runtime, rather than through any wire transport.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers every named in-process pipeline within a single call - the shape for a service with
    /// more than one in-process module, each with its own handler assembly and its own middleware
    /// stack. Route an outbound topic to a given module with <c>.UseInProcess(name)</c>.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="configure">
    /// Adds each named pipeline via <see cref="InProcessMessagingBuilder.Add(string,System.Action{IMiddlewarePipelineBuilder{BenzeneMessageContext}})"/>.
    /// </param>
    /// <returns>The same container, for chaining.</returns>
    /// <exception cref="InProcessMessagingAlreadyRegisteredException">
    /// <c>AddInProcessMessaging(...)</c> (either overload) was already called on this container.
    /// </exception>
    /// <exception cref="DuplicateInProcessPipelineException">The same pipeline name was added more than once.</exception>
    /// <example>
    /// <code>
    /// services.AddInProcessMessaging(registry =&gt; registry
    ///     .Add("billing", pipeline =&gt; pipeline.UseMessageHandlers(typeof(ChargeCardHandler).Assembly))
    ///     .Add("shipping", pipeline =&gt; pipeline.UseMessageHandlers(typeof(ReserveStockHandler).Assembly)));
    ///
    /// services.AddOutboundRouting(routing =&gt; routing
    ///     .Route("billing:charge", pipeline =&gt; pipeline.UseInProcess("billing"))
    ///     .Route("shipping:reserve", pipeline =&gt; pipeline.UseInProcess("shipping")));
    /// </code>
    /// </example>
    public static IBenzeneServiceContainer AddInProcessMessaging(
        this IBenzeneServiceContainer services, Action<InProcessMessagingBuilder> configure)
    {
        if (services.IsTypeRegistered<InProcessDispatcherRegistry>())
        {
            throw new InProcessMessagingAlreadyRegisteredException();
        }

        // Reuses the same BenzeneMessage request/response plumbing UseBenzeneMessage(HTTP/Lambda) relies
        // on, but deliberately skips AddBenzeneMessage()'s ITransportInfo("benzene") registration: a
        // service that only dispatches in-process never exposes that wire endpoint, and advertising it
        // anyway would misrepresent the service's transport surface to the mesh/descriptor.
        services.AddBenzeneMessageHandling();

        var builder = new InProcessMessagingBuilder(services);
        configure(builder);
        var dispatchersByName = builder.Build();

        services.AddSingleton(new InProcessDispatcherRegistry(dispatchersByName));
        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.InProcess));

        return services;
    }

    /// <summary>
    /// Registers the single unnamed in-process pipeline - the shape for a service with exactly one
    /// in-process module. Sugar over <see cref="AddInProcessMessaging(IBenzeneServiceContainer,Action{InProcessMessagingBuilder})"/>
    /// with one <see cref="InProcessMessagingBuilder.DefaultName"/> pipeline.
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
    /// <exception cref="InProcessMessagingAlreadyRegisteredException">
    /// <c>AddInProcessMessaging(...)</c> (either overload) was already called on this container.
    /// </exception>
    /// <example>
    /// <code>
    /// services.AddInProcessMessaging(pipeline =&gt; pipeline.UseMessageHandlers(handlers =&gt; handlers
    ///     .Add&lt;OrderCreatedHandler&gt;()));
    ///
    /// services.AddOutboundRouting(routing =&gt; routing
    ///     .Route("order:created", pipeline =&gt; pipeline.UseInProcess()));
    /// </code>
    /// </example>
    public static IBenzeneServiceContainer AddInProcessMessaging(
        this IBenzeneServiceContainer services, Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> configure) =>
        services.AddInProcessMessaging(registry => registry.Add(configure));
}
