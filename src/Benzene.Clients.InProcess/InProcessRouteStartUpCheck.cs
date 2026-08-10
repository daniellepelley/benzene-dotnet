using Benzene.Abstractions.DI;
using Benzene.Abstractions.StartUpChecks;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Every <c>.UseInProcess(name)</c> route names a pipeline that was actually registered via
/// <c>AddInProcessMessaging(...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately narrower than "every routed topic has a matching handler": that check would
/// need the topic string at the point <c>.UseInProcess(...)</c> runs, and
/// <c>OutboundRoutingBuilder.Route(topic, configure)</c> does not pass its topic into
/// <c>configure</c> - only the raw <see cref="Abstractions.Middleware.IMiddlewarePipelineBuilder{TContext}"/>.
/// Threading it through would mean changing that method's signature, used by every outbound route in
/// every transport, which is out of scope here. What <em>is</em> knowable without that change is
/// which <b>pipeline names</b> were referenced - <see cref="InProcessRouteReference"/> records one
/// per <c>.UseInProcess(name)</c> call - and this check catches the missing/misspelled ones: a typo
/// or a forgotten <c>AddInProcessMessaging(...)</c> call, at start-up instead of first send.
/// </para>
/// <para>
/// Per-topic handler existence (does the named pipeline actually handle the specific topic a route
/// is registered for) remains an honest <c>not-found</c> result at first send, exactly as any other
/// transport's unrouted-or-unhandled topic already is - see <c>work/internal-transport-design.md</c>
/// for why that trade-off was made deliberately, not dropped for lack of trying.
/// </para>
/// </remarks>
public class InProcessRouteStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "in-process-routes";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var referencedNames = resolver.GetServices<InProcessRouteReference>()
            .Select(r => r.PipelineName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (referencedNames.Length == 0)
        {
            return;
        }

        var registry = resolver.TryGetService<InProcessDispatcherRegistry>();
        var registeredNames = registry?.Names ?? Array.Empty<string>();

        var missingNames = referencedNames
            .Where(n => !registeredNames.Contains(n, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (missingNames.Length > 0)
        {
            throw new MissingInProcessPipelineException(missingNames, registeredNames);
        }
    }
}
