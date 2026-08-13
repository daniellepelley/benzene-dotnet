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
    /// <c>benzene:healthcheck</c> has no special case here: like every other <c>benzene:*</c> reserved
    /// endpoint it is excluded by default and admitted only by the ordinary rules
    /// (<see cref="ClientSdkOptions.IncludeReservedTopics"/>, or naming it in
    /// <see cref="ClientSdkOptions.Topics"/>). Generated clients are for a service's <i>domain</i>
    /// surface; Benzene's reserved endpoints are framework plumbing, deliberately kept separate, and
    /// emitting one into a client's <c>RequiredTopics</c> forced every consumer to register an
    /// outbound route it never asked for or fail the outbound-routing start-up check.
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
            => included != null
                ? included.Contains(request.Topic)
                : options.IncludeReservedTopics || !request.Reserved;

        var filteredRequests = document.Requests.Where(InScope).ToArray();

        return new EventServiceDocument(document.Info, document.Tags, filteredRequests, document.Events, document.Components)
        {
            MessageEndpoint = document.MessageEndpoint,
            Transports = document.Transports,
        };
    }
}
