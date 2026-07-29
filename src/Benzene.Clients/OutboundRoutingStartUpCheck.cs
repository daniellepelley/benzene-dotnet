using Benzene.Abstractions.DI;
using Benzene.Abstractions.StartUpChecks;

namespace Benzene.Clients;

/// <summary>
/// Every topic a generated client requires has an outbound route registered.
/// </summary>
/// <remarks>
/// <see cref="ValidateOutboundRoutingExtensions.ValidateOutboundRouting"/> has always done this and
/// nothing has ever called it — its own docs say "call once, typically right after
/// AddOutboundRouting(...) resolves", which left it to each application to remember. Registered by
/// <c>AddOutboundRouting</c> so it simply runs. The set of required topics is contract-derived, so
/// there is no ambiguity to be advisory about: a topic a client will send to with no route is a
/// failure waiting for whichever rarely-exercised code path sends it first.
/// </remarks>
public class OutboundRoutingStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "outbound-routing";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        resolver.ValidateOutboundRouting();
    }
}
