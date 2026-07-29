using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.StartUpChecks;
using Microsoft.Extensions.Logging;

namespace Benzene.Core.MessageHandlers.StartUpChecks;

/// <summary>
/// A service that registered handler dispatch and then discovered no handlers at all.
/// </summary>
/// <remarks>
/// <para>
/// Almost always means <c>AddMessageHandlers(...)</c> was given the wrong assembly — the mistake
/// <c>examples/Aws</c> carries a comment about having made once, where omitting the project's own
/// assembly left a handler undiscoverable "despite compiling and looking wired". The symptom is a 404
/// or "no handler found for topic" per message, which reads like a routing problem rather than a
/// registration one.
/// </para>
/// <para>
/// <b>Logs rather than throws</b>, and that is deliberate. A mesh-collector or probe-only deployable
/// with zero handlers is a real shape, and one codebase frequently builds several deployables that
/// each mount a subset. A check that hard-fails a legitimate arrangement is worse than the bug it
/// prevents.
/// </para>
/// </remarks>
public class EmptyHandlerRegistryStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "empty-handler-registry";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var finder = resolver.TryGetService<IMessageHandlersFinder>();
        if (finder is null || finder.FindDefinitions().Length > 0)
        {
            return;
        }

        resolver.TryGetService<ILoggerFactory>()?.CreateLogger("Benzene.StartUpChecks").LogError(
            "No message handlers were discovered. If this service is meant to handle messages, check that " +
            "AddMessageHandlers(...) was given the assembly containing your handlers — passing the wrong one " +
            "compiles and looks wired, and every message then fails to route. Ignore this if the service " +
            "deliberately has no handlers (a probe- or collector-only deployable).");
    }
}
