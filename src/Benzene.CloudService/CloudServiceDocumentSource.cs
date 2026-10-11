using Benzene.Abstractions.DI;
using Benzene.Core.MessageHandlers;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;

namespace Benzene.CloudService;

/// <summary>
/// One registration body (mesh.md §2): the contract document as JSON, its <c>registrationHash</c>, and
/// whether it is the complete document or the reduced one derivable at wire-up.
/// </summary>
internal sealed class RegistrationDocument
{
    public RegistrationDocument(string json, string registrationHash, bool complete)
    {
        Json = json;
        RegistrationHash = registrationHash;
        Complete = complete;
    }

    /// <summary>The document, exactly as registered.</summary>
    public string Json { get; }

    /// <summary>Its <c>registrationHash</c> - what heartbeats carry (mesh.md §5).</summary>
    public string RegistrationHash { get; }

    /// <summary>
    /// True for the document derived from the container's registrations - the document
    /// <c>benzene:spec</c> serves. False for the wire-up document, which names <c>events</c> and
    /// <c>sends</c> in <c>degraded</c> because their declarations live in the container.
    /// </summary>
    public bool Complete { get; }
}

/// <summary>
/// The single source of this service's registration body for the announcer: its contract document
/// (mesh.md §2), derived by the same <see cref="SpecBuilder"/> - and through the same
/// <see cref="SpecCache"/> entry - that answers <c>benzene:spec</c>, so the registered document and the
/// served one are the same document, byte for byte (the deployment-side fields included, from the
/// <see cref="ContractDocumentDeployment"/> the cloud service registers).
/// </summary>
/// <remarks>
/// With explicit handler types (<c>WithHandlers</c>) a reduced document is also available at wire-up,
/// before any resolver exists, so the service can register before its first message: its
/// <c>requests</c> come from those types, and <c>events</c> and <c>sends</c> - declared in the
/// container - are named in <c>degraded</c> rather than asserted empty (contract-document.md §1). The
/// first invocation then derives the complete document, and the announcer re-registers if it differs.
/// </remarks>
internal sealed class CloudServiceDocumentSource
{
    private readonly object _gate = new();
    private readonly RegistrationDocument? _wireUp;
    // volatile: published via double-checked locking (read outside the lock in Get) - without it a
    // weak memory model (ARM64/Graviton) could expose the reference before its fields. Benign on x64.
    private volatile RegistrationDocument? _complete;

    public CloudServiceDocumentSource(
        string service, string? serviceVersion, string? versionScheme, string messageEndpoint,
        ContractDocumentDeployment deployment, Type[]? handlerTypes)
    {
        if (handlerTypes != null)
        {
            var builder = new EventServiceDocumentBuilder()
                .AddService(service, serviceVersion, null, versionScheme)
                .AddMessageEndpoint(messageEndpoint)
                .AddDeployment(deployment)
                .AddDegraded(ContractDocumentMembers.Events)
                .AddDegraded(ContractDocumentMembers.Sends)
                .AddMessageHandlerDefinitions(new ReflectionMessageHandlersFinder(handlerTypes).FindDefinitions());
            var document = builder.Build();
            _wireUp = new RegistrationDocument(document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0),
                document.RegistrationHash, complete: false);
        }
    }

    /// <summary>The reduced document derivable at wire-up, when handler types were given; otherwise null.</summary>
    public RegistrationDocument? TryGetWireUp() => _wireUp;

    /// <summary>
    /// The complete document, derived from the invocation's container on first use: the contract
    /// document <c>benzene:spec</c> serves (contract-document.md §7), from the shared
    /// <see cref="SpecCache"/> when one is registered.
    /// </summary>
    public RegistrationDocument Get(IServiceResolver resolver)
    {
        if (_complete != null)
        {
            return _complete;
        }

        lock (_gate)
        {
            if (_complete != null)
            {
                return _complete;
            }

            var request = SpecBuilder.Normalize(null);
            var cache = resolver.TryGetService<SpecCache>();
            var json = cache != null
                ? cache.GetOrBuild(request, r => new SpecBuilder().CreateSpec(resolver, r))
                : new SpecBuilder().CreateSpec(resolver, request);
            return _complete = new RegistrationDocument(json,
                Benzene.CodeGen.Core.ContractHash.ComputeRegistrationHash(json), complete: true);
        }
    }
}
