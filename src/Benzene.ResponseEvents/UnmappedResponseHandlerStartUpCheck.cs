using Benzene.Abstractions.DI;
using Benzene.Abstractions.StartUpChecks;

namespace Benzene.ResponseEvents;

/// <summary>
/// Logs handlers that return a payload on a topic no response-event mapping covers.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResponseEventDiagnosticsExtensions.LogUnmappedResponseHandlers"/> has always done this
/// and nothing has ever called it. Running it here costs nothing and puts the findings in front of
/// someone.
/// </para>
/// <para>
/// <b>It logs and never throws</b>, deliberately, and unlike the other checks it stays that way. Its
/// own doc comment has the reason: a transport-agnostic handler that legitimately publishes nothing
/// is indistinguishable from one that forgot to, so false positives are structural rather than
/// incidental. A check that cannot tell the two apart must not be able to fail a build.
/// </para>
/// </remarks>
public class UnmappedResponseHandlerStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "unmapped-response-handlers";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        resolver.LogUnmappedResponseHandlers();
    }
}
