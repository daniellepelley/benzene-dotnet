using Benzene.CodeGen.Core;
using Benzene.CodeGen.Core.Writers;
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
    /// (<c>{Namespace}.{ClientName}</c>); <see cref="ClientSdkOptions.ServiceName"/> does not name any
    /// client (each is named from its own topic) - it is used only to name the aggregate DI
    /// registration extension, which is skipped when no service name is given.
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

        var files = new List<ICodeFile>();
        var clientNames = new List<string>();

        foreach (var request in scopedDocument.Requests)
        {
            var clientName = _clientNameFormatter.Create(request.Topic, request.Request);
            clientNames.Add(clientName);
            files.AddRange(BuildForTopic(scopedDocument, request, clientName));
        }

        // Each atomic client already carries its own Add{Client}ServiceClient() extension (emitted by
        // the inner MessageClientSdkBuilder, inside that client's own folder/namespace), so a consumer
        // that drops in one folder for one topic still gets its registration. This adds the other half:
        // one Add{Service}Clients() at the root that calls every per-topic extension, so a consumer
        // taking several topics off the same service registers them in one line instead of N. It needs
        // a service name to be called anything sensible - without one there is nothing to aggregate
        // under, so it is simply not emitted (and never when there are no clients at all).
        if (clientNames.Count > 0 && !string.IsNullOrWhiteSpace(_options.ServiceName))
        {
            files.Add(new CodeFile($"{_options.ServiceName}ClientsRegistration.cs", BuildAggregateRegistration(clientNames)));
        }

        return files.ToArray();
    }

    // One extension registering every per-topic client of this service, delegating to each client's
    // own generated extension rather than repeating the interface/implementation/lifetime triple.
    private string[] BuildAggregateRegistration(IReadOnlyCollection<string> clientNames)
    {
        var serviceName = _options.ServiceName;
        var lineWriter = new LineWriter();

        lineWriter.WriteLine("using System.Diagnostics.CodeAnalysis;");
        lineWriter.WriteLine("using Benzene.Abstractions.DI;");
        foreach (var clientName in clientNames)
        {
            lineWriter.WriteLine($"using {_options.Namespace}.{clientName};");
        }
        lineWriter.WriteLine("");

        lineWriter.WriteLine($"namespace {_options.Namespace}");
        lineWriter.WriteLine("{");
        lineWriter.WriteLine("[ExcludeFromCodeCoverage]", 1);
        lineWriter.WriteLine($"public static class {serviceName}ClientsRegistration", 1);
        lineWriter.WriteLine("{", 1);
        lineWriter.WriteLine($"/// <summary>Registers every generated {serviceName} topic client.</summary>", 2);
        lineWriter.WriteLine($"public static IBenzeneServiceContainer Add{serviceName}Clients(this IBenzeneServiceContainer container)", 2);
        lineWriter.WriteLine("{", 2);
        foreach (var clientName in clientNames)
        {
            lineWriter.WriteLine($"container.Add{clientName}ServiceClient();", 3);
        }
        lineWriter.WriteLine("return container;", 3);
        lineWriter.WriteLine("}", 2);
        lineWriter.WriteLine("}", 1);
        lineWriter.WriteLine("}");

        return lineWriter.GetLines();
    }

    private ICodeFile[] BuildForTopic(EventServiceDocument document, RequestResponse request, string clientName)
    {
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
            // This is contract-document.md §5.3's topic-scoped shape: its contract hash (§6.2) does
            // not strip a reserved entry entirely (only its flag, already reflected by `filtered`
            // above being narrowed to this one request) - an atomic client explicitly built for one
            // reserved topic hashes that topic's contract, not an empty one.
            IsTopicScopedForHash = true,
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
        return SchemaClosure.Reachable(document.Components.Schemas, request.Request, request.Response);
    }
}
