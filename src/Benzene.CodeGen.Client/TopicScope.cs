using Benzene.Schema.OpenApi.EventService;

namespace Benzene.CodeGen.Client;

/// <summary>
/// The single topic-scoping implementation shared by <see cref="MessageClientSdkBuilder"/> and
/// <see cref="AtomicClientSdkBuilder"/>, applied once at the top of each builder's
/// <c>BuildCodeFiles</c> before any of its per-field iteration sites (methods, interface,
/// <c>RequiredTopics</c>, or - in atomic mode - which per-topic clients exist) run. Filtering the
/// document once this way, rather than re-implementing the same include/exclude rule at each site,
/// is what makes those sites unable to disagree with each other - see
/// work/spec-mesh-tooling-implementation-plan.md Phase 3b step 3.
/// </summary>
internal static class TopicScope
{
    /// <summary>
    /// Projects <paramref name="document"/>'s <see cref="EventServiceDocument.Requests"/> down to
    /// the topics in scope per <paramref name="options"/>, returning a new document with everything
    /// else (info, tags, events, components, message endpoint, transports) unchanged.
    /// <c>benzene:healthcheck</c> is always excluded from the projected <c>Requests</c>, regardless
    /// of <see cref="ClientSdkOptions.Topics"/> or <see cref="ClientSdkOptions.IncludeReservedTopics"/>:
    /// <see cref="MessageClientSdkBuilder"/> already emits its <c>HealthCheckAsync()</c> method and
    /// its <c>RequiredTopics</c> entry unconditionally, outside the per-request loops entirely, so a
    /// caller never needs to (and never can usefully) name it in an include-list - keeping a matching
    /// entry here would just duplicate that <c>RequiredTopics</c> entry and, in topic-client mode,
    /// spawn a redundant dedicated health-check client on top of every other client's own
    /// already-emitted health check.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="ClientSdkOptions.Topics"/> names a topic the document does not have.
    /// </exception>
    public static EventServiceDocument Apply(EventServiceDocument document, ClientSdkOptions options)
    {
        var requestedTopics = options.Topics is { Count: > 0 } ? options.Topics : null;

        if (requestedTopics != null)
        {
            var known = new HashSet<string>(document.Requests.Select(r => r.Topic), StringComparer.Ordinal);
            var unknown = requestedTopics.Where(t => !known.Contains(t)).ToArray();
            if (unknown.Length > 0)
            {
                throw new ArgumentException(
                    $"--topics names topic(s) not present in the document: {string.Join(", ", unknown)}. " +
                    $"Valid topics: {string.Join(", ", known.OrderBy(t => t, StringComparer.Ordinal))}.");
            }
        }

        var included = requestedTopics != null ? new HashSet<string>(requestedTopics, StringComparer.Ordinal) : null;

        bool InScope(RequestResponse request)
        {
            if (request.Topic == Benzene.Abstractions.BenzeneTopic.HealthCheck)
            {
                return false;
            }

            return included != null
                ? included.Contains(request.Topic)
                : options.IncludeReservedTopics || !request.Reserved;
        }

        var filteredRequests = document.Requests.Where(InScope).ToArray();

        return new EventServiceDocument(document.Info, document.Tags, filteredRequests, document.Events, document.Components)
        {
            MessageEndpoint = document.MessageEndpoint,
            Transports = document.Transports,
        };
    }
}
