using Benzene.Abstractions.DI;
using Benzene.HealthChecks.Core;

namespace Benzene.Clients.HealthChecks;

/// <summary>
/// Registration helpers for the consumer-side contract-drift check (<see cref="ClientHealthCheck"/>).
/// Add these to the <em>contracts</em> diagnostic topic via <c>UseContractsCheck(...)</c> - never a
/// liveness or readiness probe (see <see cref="ClientHealthCheck"/> and
/// <c>docs/kubernetes-health-checks.md</c>).
/// </summary>
public static class ContractHealthCheckExtensions
{
    /// <summary>
    /// Registers a <see cref="ClientHealthCheck"/> for a downstream service, resolving its
    /// <typeparamref name="TClient"/> client from DI each time checks run.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TClient"/> has to be an <see cref="IHasHealthCheck"/>, which in practice
    /// means a hand-written client: CodeGen-generated clients cover a service's <em>domain</em> topics
    /// only and do not implement it. For the standard downstream health call there is nothing to
    /// hand-write - use <see cref="AddServiceCheck"/>, which is built on the library's own
    /// <see cref="ServiceHealthCheckClient"/>.
    /// </remarks>
    /// <typeparam name="TClient">The client type for the downstream service (an <see cref="IHasHealthCheck"/>), e.g. <c>IOrderServiceClient</c>.</typeparam>
    /// <param name="builder">The health check builder to register against.</param>
    /// <param name="serviceName">The downstream service's name, used as the check's identifier and dependency name.</param>
    public static IHealthCheckBuilder AddContractCheck<TClient>(this IHealthCheckBuilder builder, string serviceName)
        where TClient : class, IHasHealthCheck
    {
        return builder.AddHealthCheck(resolver =>
            new ClientHealthCheck(serviceName, resolver.GetService<TClient>()));
    }

    /// <summary>
    /// Registers a <see cref="ClientHealthCheck"/> for a downstream service against an explicit client
    /// instance (rather than resolving one from DI).
    /// </summary>
    /// <param name="builder">The health check builder to register against.</param>
    /// <param name="serviceName">The downstream service's name, used as the check's identifier and dependency name.</param>
    /// <param name="client">The client for the downstream service.</param>
    public static IHealthCheckBuilder AddContractCheck(this IHealthCheckBuilder builder, string serviceName, IHasHealthCheck client)
    {
        return builder.AddHealthCheck(new ClientHealthCheck(serviceName, client));
    }

    /// <summary>
    /// Registers a <see cref="ClientHealthCheck"/> for a downstream service using the library's built-in
    /// <see cref="ServiceHealthCheckClient"/> - <strong>no client type of any kind required</strong>.
    /// The <see cref="IBenzeneMessageSender"/> is resolved from the container each time checks run, the
    /// same way <see cref="AddContractCheck{TClient}"/> resolves its client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ordinary way to health-check a downstream: the health-check payload is standard and
    /// known up front, so nothing needs generating or hand-writing for it. Supply
    /// <paramref name="expectedContractHash"/> - a generated client's <c>HashCode</c> property, e.g.
    /// <c>new PaymentsServiceClient(sender).HashCode</c> - to get contract-drift reporting on top of
    /// reachability; omit it for a reachability-only check.
    /// </para>
    /// <para>
    /// <strong>The check sends <c>benzene:healthcheck</c></strong>
    /// (<see cref="Benzene.Abstractions.BenzeneTopic.HealthCheck"/>), so the consumer must register an
    /// outbound route for that topic - an explicit opt-in, rather than something forced on every
    /// consumer of a generated client. See <see cref="ServiceHealthCheckClient"/>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The health check builder to register against.</param>
    /// <param name="serviceName">The downstream service's name, used as the check's identifier and dependency name.</param>
    /// <param name="expectedContractHash">Optional contract hash to compare the provider's published hash against; omitted means reachability only.</param>
    public static IHealthCheckBuilder AddServiceCheck(this IHealthCheckBuilder builder, string serviceName, string? expectedContractHash = null)
    {
        return builder.AddHealthCheck(resolver =>
            new ClientHealthCheck(serviceName,
                new ServiceHealthCheckClient(resolver.GetService<IBenzeneMessageSender>(), expectedContractHash)));
    }
}
