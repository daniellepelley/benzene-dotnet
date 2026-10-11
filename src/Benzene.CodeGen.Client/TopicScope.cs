using Benzene.Abstractions;
using Benzene.Schema.OpenApi.EventService;

namespace Benzene.CodeGen.Client;

/// <summary>
/// The single topic-scoping implementation shared by <see cref="MessageClientSdkBuilder"/> and
/// <see cref="AtomicClientSdkBuilder"/>, applied once at the top of each builder's
/// <c>BuildCodeFiles</c> before any of its per-field iteration sites (methods, interface,
/// <c>RequiredTopics</c>, or - in atomic mode - which per-topic clients exist) run. Filtering the
/// document once this way, rather than re-implementing the same include/exclude rule at each site,
/// is what makes those sites unable to disagree with each other - see
/// work/archive/spec-mesh-tooling-implementation-plan-2026-08.md Phase 3b step 3.
/// </summary>
internal static class TopicScope
{
    /// <summary>
    /// Projects <paramref name="document"/>'s <see cref="EventServiceDocument.Requests"/> down to the
    /// include-list in <paramref name="options"/> (contract-document.md §5.2), returning a new document
    /// with everything else (identity, events, sends, components, message endpoint, transports)
    /// unchanged; its hashes are recomputed over the projection. With no include-list every request is
    /// in scope. A document never lists a <c>benzene:</c> topic (§1), and any such entry in a document
    /// from elsewhere is dropped here too (§5.1): a generated client covers a service's own topics only.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="ClientSdkOptions.Topics"/> names a topic the document does not have.
    /// </exception>
    public static EventServiceDocument Apply(EventServiceDocument document, ClientSdkOptions options)
    {
        var requestedTopics = options.Topics is { Count: > 0 } ? options.Topics : null;
        var ownRequests = document.Requests.Where(r => !BenzeneTopic.IsReserved(r.Topic)).ToArray();

        if (requestedTopics != null)
        {
            var known = new HashSet<string>(ownRequests.Select(r => r.Topic), StringComparer.Ordinal);
            var unknown = requestedTopics.Where(t => !known.Contains(t)).ToArray();
            if (unknown.Length > 0)
            {
                throw new ArgumentException(
                    $"--topics names topic(s) not present in the document: {string.Join(", ", unknown)}. " +
                    $"Valid topics: {string.Join(", ", known.OrderBy(t => t, StringComparer.Ordinal))}.");
            }
        }

        var included = requestedTopics != null ? new HashSet<string>(requestedTopics, StringComparer.Ordinal) : null;
        var filteredRequests = ownRequests.Where(r => included == null || included.Contains(r.Topic)).ToArray();

        return document.With(requests: filteredRequests,
            events: document.Events.Where(e => !BenzeneTopic.IsReserved(e.Topic)).ToArray(),
            sends: document.Sends.Where(e => !BenzeneTopic.IsReserved(e.Topic)).ToArray());
    }

    /// <summary>
    /// contract-document.md §5.3's topic-scoped projection: <c>requests</c> with exactly
    /// <paramref name="request"/>, <c>events</c> and <c>sends</c> empty, <c>components.schemas</c>
    /// exactly the schemas it reaches, every other field unchanged; the hashes follow the projection.
    /// </summary>
    public static EventServiceDocument ProjectTopic(EventServiceDocument document, RequestResponse request)
    {
        return document.With(
            requests: new[] { request },
            events: Array.Empty<Event>(),
            sends: Array.Empty<Send>(),
            components: new Microsoft.OpenApi.Models.OpenApiComponents
            {
                Schemas = SchemaClosure.Reachable(document.Components.Schemas, request.Request, request.Response)
            });
    }
}
