using Benzene.Abstractions;
using Benzene.Abstractions.Results;
using Benzene.HealthChecks.Core;
using Benzene.Results;

namespace Benzene.Clients.HealthChecks;

/// <summary>
/// The built-in downstream health call: sends Benzene's reserved
/// <see cref="BenzeneTopic.HealthCheck"/> topic to a provider over <see cref="IBenzeneMessageSender"/>
/// and (when an expected contract hash is supplied) annotates the answer with the contract-drift
/// verdict, exactly as a hand-written <see cref="IHasHealthCheck"/> client would.
/// <para>
/// Calling another service's health check is a <em>health-check</em> concern, like pinging a database
/// or an SQS queue: an orchestrator that depends on an unhealthy service is by definition unhealthy.
/// It needs no generated code, because the health-check request/response payload is standard and known
/// up front - fixed by the libraries (<c>Void</c> in, <see cref="HealthCheckResponse"/> out) -
/// unlike a service's domain payloads, which differ per service and are the whole reason domain
/// clients are generated at all. So this lives here, in the health-check library, and a generated
/// client covers domain topics only.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Requires an outbound route for <c>benzene:healthcheck</c>.</strong> This check sends on
/// <see cref="BenzeneTopic.HealthCheck"/>, so the consumer must register a route for that topic (via
/// <c>AddOutboundRouting(...)</c>/<c>OutboundRoutingBuilder.Route</c>) pointed at the provider it
/// wants to probe, or the send fails with <c>UnroutedTopicException</c>. That registration is a
/// deliberate opt-in: probing a downstream's health is a choice about a specific dependency (and over
/// a transport that can actually answer - a fire-and-forget queue cannot), not something every
/// consumer of a generated client should be forced into. It used to be forced, and that is precisely
/// why generated clients no longer carry a health check.
/// </para>
/// <para>
/// Register it with <see cref="ContractHealthCheckExtensions.AddServiceCheck"/> on the <em>contracts</em>
/// diagnostic topic (<c>UseContractsCheck</c>), never a liveness or readiness probe - see
/// <see cref="ClientHealthCheck"/> and <c>docs/kubernetes-health-checks.md</c>.
/// </para>
/// </remarks>
public class ServiceHealthCheckClient : IHasHealthCheck
{
    private readonly IBenzeneMessageSender _sender;
    private readonly string? _expectedContractHash;

    /// <summary>Initializes a new instance of the <see cref="ServiceHealthCheckClient"/> class.</summary>
    /// <param name="sender">The outbound sender used to send <see cref="BenzeneTopic.HealthCheck"/> to the downstream provider.</param>
    /// <param name="expectedContractHash">
    /// Optional. The contract hash this consumer expects the provider to publish - a generated client
    /// exposes exactly this as its <c>HashCode</c> property, so a consumer can pass
    /// <c>theGeneratedClient.HashCode</c>. Supplied: the provider's response is checked for contract
    /// drift as well as reachability. Omitted (the default): reachability only - no hash is compared
    /// and no <see cref="ClientHashMatch"/> is written, so nothing can report drift it has no basis to
    /// claim.
    /// </param>
    public ServiceHealthCheckClient(IBenzeneMessageSender sender, string? expectedContractHash = null)
    {
        _sender = sender;
        _expectedContractHash = expectedContractHash;
    }

    /// <summary>
    /// The contract hash this check compares against, or the empty string when none was supplied
    /// (reachability-only mode). <see cref="IHasHealthCheck.HashCode"/> is non-nullable, and an empty
    /// hash is never compared with anything - <see cref="HealthCheckAsync"/> skips the drift step
    /// entirely rather than comparing against "".
    /// </summary>
    public string HashCode => _expectedContractHash ?? string.Empty;

    /// <inheritdoc />
    public async Task<IBenzeneResult<HealthCheckResponse>> HealthCheckAsync()
    {
        // Benzene.Abstractions.Results.Void is the established "no meaningful request" payload: every
        // server-side handler for benzene:healthcheck reads the topic, not the body. Fully qualified
        // because a bare "Void" is ambiguous with System.Void (CS0104) once ImplicitUsings puts System
        // in scope alongside Benzene.Abstractions.Results.
        var benzeneResult = await _sender
            .SendAsync<Benzene.Abstractions.Results.Void, HealthCheckResponse>(
                BenzeneTopic.HealthCheck, new Benzene.Abstractions.Results.Void());

        // Unreachable / no payload: nothing to annotate. ClientHealthCheck turns this into Failed.
        if (benzeneResult.Payload == null)
        {
            return benzeneResult;
        }

        // Reachability-only: pass the provider's response straight through, un-annotated. Running the
        // processor against an empty hash would manufacture a ClientHashMatch whose IsMatch is false
        // against a real ServiceHashCode - which ClientHealthCheck reads as genuine drift and reports
        // as a Warning. No expected hash means no opinion on drift, not drift.
        if (string.IsNullOrEmpty(_expectedContractHash))
        {
            return benzeneResult;
        }

        var annotated = ClientHealthCheckProcessor.Process(benzeneResult.Payload, _expectedContractHash) as HealthCheckResponse;
        return BenzeneResult.Set(benzeneResult.Status, annotated, benzeneResult.IsSuccessful);
    }
}
