using Benzene.Abstractions.Results;
using Benzene.Clients.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Results;

namespace Benzene.Examples.Mesh.OrdersService.Clients;

/// <summary>
/// A canned stand-in for the library's own <see cref="ServiceHealthCheckClient"/>, so this demo can
/// show a consumer-side contract-drift check with no live payments-api and no outbound route to it. The
/// real thing sends <c>benzene:healthcheck</c> to the provider over <c>IBenzeneMessageSender</c> and
/// runs <see cref="ClientHealthCheckProcessor"/> to compare the provider's live contract hash against
/// the one the consumer expects; this fakes the send with canned data (the same deterministic style as
/// the rest of this example) and runs the identical processor, so the drift verdict it produces is real.
/// </summary>
/// <remarks>
/// A <c>Benzene.CodeGen.Client</c>-generated client is <em>not</em> an <see cref="IHasHealthCheck"/>:
/// generated clients cover a service's domain topics only, never Benzene's reserved <c>benzene:*</c>
/// endpoints (they do expose a <c>HashCode</c>, which is what you would pass to
/// <c>AddServiceCheck("Payments", client.HashCode)</c>). Hand-writing an <see cref="IHasHealthCheck"/>
/// like this one is only needed to fake the call, as here.
///
/// This check belongs on orders-api's <c>contracts</c> diagnostic topic (see <c>Startup</c>), never
/// its <c>/healthcheck</c> readiness surface: it reaches out to payments-api, so a probe that
/// included it could de-route or restart orders-api just because a downstream drifted or went slow.
/// </remarks>
public class PaymentsContractClient : IHasHealthCheck
{
    // The payments-api contract hash this consumer expects (in a real service: the generated client's
    // own HashCode)...
    public string HashCode => "payments-contract-v1";

    public Task<IBenzeneResult<HealthCheckResponse>> HealthCheckAsync()
    {
        // ...versus the hash payments-api currently publishes from its schema health check. They
        // differ here, so this models genuine contract drift - the same drift the demo's payments-api
        // earns at the mesh-aggregator level, seen from the consumer's side at runtime instead.
        var providerResponse = new HealthCheckResponse(true, new Dictionary<string, HealthCheckResult>
        {
            [SchemaHealthCheckConstants.Type] = (HealthCheckResult)HealthCheckResult.CreateInstance(
                true, SchemaHealthCheckConstants.Type,
                new Dictionary<string, object> { [SchemaHealthCheckConstants.HashCodeKey] = "payments-contract-v2" }),
        });

        var annotated = (HealthCheckResponse)ClientHealthCheckProcessor.Process(providerResponse, HashCode);
        return Task.FromResult(BenzeneResult.Ok(annotated));
    }
}
