namespace Benzene.Clients.InProcess;

/// <summary>
/// Thrown when <c>AddInProcessMessaging(...)</c> is called a second time on the same container.
/// </summary>
/// <remarks>
/// Before named pipelines, a second call silently shadowed the first: <c>AddSingleton</c> is
/// additive, so the in-process dispatcher a route resolved depended on registration order, and a
/// module registered by an earlier call could vanish from routing with no error anywhere. Rather
/// than retrofit a shared, cross-call registry to detect and merge repeat calls, this container
/// abstraction gives no way to fetch a previously-registered singleton *instance* back out during
/// <c>ConfigureServices</c> - only <see cref="Abstractions.DI.IBenzeneServiceContainer.IsTypeRegistered{TService}"/>
/// to check presence. So instead, one call is the contract: every module's pipeline is added within
/// that one call via <see cref="InProcessMessagingBuilder.Add(string,System.Action{Abstractions.Middleware.IMiddlewarePipelineBuilder{Core.Messages.BenzeneMessage.BenzeneMessageContext}})"/>,
/// mirroring how <c>OutboundRoutingBuilder.Route</c> accumulates many topics inside one
/// <c>AddOutboundRouting(...)</c> call. A second top-level call is almost always the mistake this
/// exception exists to catch, not a deliberate second module - the fix is combining both calls into
/// one, not adding a second.
/// </remarks>
public class InProcessMessagingAlreadyRegisteredException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessMessagingAlreadyRegisteredException"/> class.
    /// </summary>
    public InProcessMessagingAlreadyRegisteredException()
        : base("AddInProcessMessaging(...) was already called on this container. Register every " +
               "in-process module's pipeline within a single call: " +
               "AddInProcessMessaging(registry => registry.Add(\"billing\", ...).Add(\"shipping\", ...)) - " +
               "not one AddInProcessMessaging(...) call per module.")
    {
    }
}
