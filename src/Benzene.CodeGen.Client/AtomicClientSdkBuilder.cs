using Benzene.CodeGen.Core;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi.Models;

namespace Benzene.CodeGen.Client;

/// <summary>
/// Generates a small, single-topic ("atomic") client per topic instead of one client covering the
/// whole service. A consumer that calls only one topic gets a client scoped to just that topic: its
/// <c>RequiredTopics</c> startup-validation array and its contract hash cover only that topic's
/// contract, so an unrelated change elsewhere on the producing service neither drags in unused
/// surface nor invalidates this client - better cohesion when a service depends on one topic rather
/// than a whole service.
/// </summary>
/// <remarks>
/// Reuses <see cref="MessageClientSdkBuilder"/> against a per-topic filtered
/// <see cref="EventServiceDocument"/> (one request, only the component schemas that request reaches),
/// so the generated client/interface/routing/DTO shapes match the service-level client exactly and
/// the contract hash falls out topic-scoped. Each topic is emitted under its own client name
/// (from <see cref="TopicMethodName"/> by default, e.g. <c>user:create</c> → <c>UserCreate</c>), so
/// one document yields one atomic client set per topic.
/// </remarks>
public class AtomicClientSdkBuilder : ICodeBuilder<EventServiceDocument>
{
    private readonly ClientSdkOptions _options;
    private readonly IMethodName _clientNameFormatter;

    /// <summary>Initializes an atomic client builder that skips reserved utility topics.</summary>
    /// <param name="baseNamespace">The base namespace for the generated clients (each client lands in <c>{baseNamespace}.{ClientName}</c>).</param>
    public AtomicClientSdkBuilder(string baseNamespace)
        : this(baseNamespace, new TopicMethodName(), false)
    { }

    /// <summary>Initializes an atomic client builder.</summary>
    /// <param name="baseNamespace">The base namespace for the generated clients.</param>
    /// <param name="clientNameFormatter">Derives each topic's client name from its topic id (defaults to <see cref="TopicMethodName"/>).</param>
    /// <param name="includeReservedTopics">When false (the default), reserved Benzene utility topics (spec/health/mesh/…) are skipped so only domain topics get atomic clients.</param>
    public AtomicClientSdkBuilder(string baseNamespace, IMethodName clientNameFormatter, bool includeReservedTopics)
        : this(new ClientSdkOptions { Namespace = baseNamespace, IncludeReservedTopics = includeReservedTopics }, clientNameFormatter)
    { }

    /// <summary>
    /// Initializes an atomic client builder from <paramref name="options"/>.
    /// <see cref="ClientSdkOptions.Namespace"/> is the root each per-topic client lands under
    /// (<c>{Namespace}.{ClientName}</c>); <see cref="ClientSdkOptions.ServiceName"/> is unused, since
    /// each client is named from its own topic.
    /// </summary>
    public AtomicClientSdkBuilder(ClientSdkOptions options)
        : this(options, new TopicMethodName())
    { }

    public AtomicClientSdkBuilder(ClientSdkOptions options, IMethodName clientNameFormatter)
    {
        _options = options;
        _clientNameFormatter = clientNameFormatter;
    }

    /// <inheritdoc />
    public ICodeFile[] BuildCodeFiles(EventServiceDocument eventServiceDocument)
    {
        var scopedDocument = TopicScope.Apply(eventServiceDocument, _options);

        return scopedDocument.Requests
            .SelectMany(request => BuildForTopic(scopedDocument, request))
            .ToArray();
    }

    private ICodeFile[] BuildForTopic(EventServiceDocument document, RequestResponse request)
    {
        var clientName = _clientNameFormatter.Create(request.Topic, request.Request);

        var filtered = new EventServiceDocument(
            document.Info,
            document.Tags,
            new[] { request },
            Array.Empty<Event>(),
            new OpenApiComponents { Schemas = ReachableSchemas(document, request) })
        {
            MessageEndpoint = document.MessageEndpoint,
            Transports = document.Transports,
        };

        // Each atomic client is fully self-contained in its own namespace ({options.Namespace}.{clientName}),
        // so its files go under a per-client folder. This keeps a DTO shared by two topics (generated
        // once per client, each in that client's namespace) from colliding on a flat filename, and lets
        // a consumer drop a single client folder in for the one topic it calls. The inner builder's
        // Topics is pinned to exactly this one topic (rather than re-deriving from _options) since
        // `filtered` already carries only that one request - re-applying the outer include-list/
        // reserved-topic policy here would be redundant at best and, for a reserved topic admitted by
        // an explicit --topics entry, wrongly re-excluded at worst.
        var innerOptions = new ClientSdkOptions
        {
            ServiceName = clientName,
            Namespace = $"{_options.Namespace}.{clientName}",
            Topics = new[] { request.Topic },
            IncludeReservedTopics = true,
        };

        return new MessageClientSdkBuilder(innerOptions)
            .BuildCodeFiles(filtered)
            .Select(file => new CodeFile($"{clientName}/{file.Name}", file.Lines) as ICodeFile)
            .ToArray();
    }

    // Collects only the component schemas reachable from this one topic's request/response, so the
    // atomic client emits (and hashes) just that topic's DTOs rather than the whole service catalogue.
    private static IDictionary<string, OpenApiSchema> ReachableSchemas(EventServiceDocument document, RequestResponse request)
    {
        var catalogue = document.Components.Schemas;
        var reached = new HashSet<string>();

        void Walk(OpenApiSchema? schema)
        {
            if (schema == null)
            {
                return;
            }

            var referenceId = schema.Reference?.Id;
            // reached.Add short-circuits already-visited components, so reference cycles terminate.
            if (referenceId != null && catalogue.ContainsKey(referenceId) && reached.Add(referenceId))
            {
                Walk(catalogue[referenceId]);
            }

            Walk(schema.Items);
            Walk(schema.AdditionalProperties);
            foreach (var property in schema.Properties.Values)
            {
                Walk(property);
            }
            foreach (var composed in schema.AllOf.Concat(schema.AnyOf).Concat(schema.OneOf))
            {
                Walk(composed);
            }
        }

        Walk(request.Request);
        Walk(request.Response);

        return catalogue
            .Where(entry => reached.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }
}
