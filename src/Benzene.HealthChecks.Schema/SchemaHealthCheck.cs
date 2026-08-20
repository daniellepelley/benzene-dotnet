using Benzene.Abstractions.MessageHandlers;
using Benzene.CodeGen.Core;
using Benzene.HealthChecks.Core;

namespace Benzene.HealthChecks.Schema;

/// <summary>
/// A provider-side contract health check: it hashes the service's current message contract (every
/// registered handler's topic + request/response schema) and publishes the hash so consumers can
/// tell whether the contract still matches what their generated client was built against.
/// </summary>
/// <remarks>
/// The hash is computed with the same <see cref="ContractHash.Compute(IMessageHandlerDefinition[], bool)"/>
/// that <c>Benzene.CodeGen.Client</c>'s <c>MessageClientSdkBuilder</c> bakes into a generated
/// <c>{Service}ServiceClient.HashCode</c> - including its domain projection (contract-document.md
/// §6.2: reserved <c>benzene:*</c> topics excluded entirely from the whole-service hash), which is
/// why this hashes <em>every</em> registered handler rather than pre-filtering them: the domain
/// projection is <see cref="ContractHash"/>'s job, applied identically on both sides, so a consumer's
/// baked-in hash and this live hash stay directly comparable (contract drift = mismatch). Before this
/// alignment, a default-generated client's hash (already domain-scoped by
/// <c>Benzene.CodeGen.Client.TopicScope</c> before hashing) could never match this provider-side hash
/// of the full, reserved-topics-included handler set - see
/// work/archive/cross-language-clients-plan-2026-08.md Phase 2 step 3. The consumer side of the loop is
/// <c>Benzene.Clients.HealthChecks.ClientHealthCheckProcessor</c>.
/// </remarks>
public class SchemaHealthCheck : IHealthCheck
{
    private readonly IMessageHandlerDefinitionLookUp _lookUp;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaHealthCheck"/> class.
    /// </summary>
    /// <param name="lookUp">The lookup used to enumerate the service's registered handlers.</param>
    public SchemaHealthCheck(IMessageHandlerDefinitionLookUp lookUp)
    {
        _lookUp = lookUp;
    }

    /// <inheritdoc />
    public string Type => SchemaHealthCheckConstants.Type;

    /// <inheritdoc />
    public Task<IHealthCheckResult> ExecuteAsync()
    {
        var hashCode = ContractHash.Compute(_lookUp.GetAllHandlers());

        var data = new Dictionary<string, object>
        {
            [SchemaHealthCheckConstants.HashCodeKey] = hashCode,
        };

        return Task.FromResult(HealthCheckResult.CreateInstance(true, SchemaHealthCheckConstants.Type, data));
    }
}
