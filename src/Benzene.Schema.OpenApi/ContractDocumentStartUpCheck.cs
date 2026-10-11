using Benzene.Abstractions.DI;
using Benzene.Abstractions.StartUpChecks;

namespace Benzene.Schema.OpenApi;

/// <summary>
/// Derives the contract document once at start-up, so a derivation error - two different types
/// claiming one component name (contract-document.md §4.1 rule 4), or a (topic, version) declared both
/// as an event and as a send (§3.3) - fails the host before any message is handled, instead of on the
/// first <c>benzene:spec</c> request. The derived document is kept in the <see cref="SpecCache"/> when
/// one is registered, so the work is not repeated.
/// </summary>
/// <remarks>
/// Opt-in for manual composition (<see cref="Extensions.AddContractDocumentStartUpCheck"/>): deriving
/// every schema costs a cold start, which a short-lived host such as a Lambda function may not want to
/// pay. <c>UseBenzeneCloudService</c> registers it by default, with
/// <c>WithoutContractDocumentStartUpCheck()</c> as the opt-out. Build-time
/// derivation (<c>benzene-descriptor --emit spec</c>, <c>Benzene.CodeGen.Build</c>) catches the same
/// errors without it.
/// </remarks>
public sealed class ContractDocumentStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "contract-document";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var request = new SpecRequest(SpecBuilder.BenzeneType, SpecBuilder.JsonFormat);
        var cache = resolver.TryGetService<SpecCache>();
        if (cache != null)
        {
            cache.GetOrBuild(request, r => new SpecBuilder().CreateSpec(resolver, r));
            return;
        }

        new SpecBuilder().CreateSpec(resolver, request);
    }
}
