namespace Benzene.CodeGen.Client;

/// <summary>
/// Configuration for <see cref="MessageClientSdkBuilder"/> and <see cref="AtomicClientSdkBuilder"/>:
/// naming (service name, generated namespace) and topic scoping (an include-list plus the reserved-
/// topic policy). Both builders' legacy positional constructors delegate to an options-based
/// constructor built from an instance of this class with today's defaults, so existing call sites
/// and golden-file tests keep behaving identically - see
/// work/spec-mesh-tooling-implementation-plan.md Phase 3b step 1.
/// </summary>
public class ClientSdkOptions
{
    /// <summary>
    /// The service name used for class/file naming (e.g. <c>"User"</c> produces
    /// <c>UserServiceClient.cs</c> / <c>IUserServiceClient.cs</c>). Required by
    /// <see cref="MessageClientSdkBuilder"/>; ignored by <see cref="AtomicClientSdkBuilder"/>, which
    /// derives each topic's client name from the topic itself instead.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// The full generated namespace, used exactly - no magic <c>.{ServiceName}</c> suffix - across
    /// the client class, its interface and its DTOs, all in one namespace. Required.
    /// <see cref="AtomicClientSdkBuilder"/> still appends <c>.{ClientName}</c> per topic below this
    /// root, since each atomic client is self-contained in its own namespace.
    /// </summary>
    public required string Namespace { get; set; }

    /// <summary>
    /// The topic include-list: only these topics are in scope. Null or empty means "every topic",
    /// subject to <see cref="IncludeReservedTopics"/>. A topic named here that the document does not
    /// have fails loud rather than silently generating nothing for it.
    /// </summary>
    public IReadOnlyCollection<string>? Topics { get; set; }

    /// <summary>
    /// When false (the default) and <see cref="Topics"/> is not set, every reserved Benzene utility
    /// topic (<c>benzene:spec</c>, <c>benzene:mesh</c>, <c>benzene:healthcheck</c>, ... - see
    /// <see cref="Benzene.Schema.OpenApi.ReservedTopics"/>) is excluded, so a generated client only
    /// covers a service's domain surface by default.
    /// Ignored when <see cref="Topics"/> explicitly names a topic: an explicit ask always wins over
    /// this default.
    /// </summary>
    public bool IncludeReservedTopics { get; set; }

    /// <summary>
    /// Selects which of <c>contract-document.md</c> §6.2's two <c>normalize()</c> behaviours
    /// <see cref="MessageClientSdkBuilder"/>'s embedded contract hash uses for reserved
    /// <see cref="Benzene.Schema.OpenApi.EventService.RequestResponse"/> entries: <c>false</c> (the
    /// default) strips them entirely, matching the domain-only whole-service/service-level
    /// projection; <c>true</c> - set only by <see cref="AtomicClientSdkBuilder"/> on the single-topic
    /// document it builds internally, §5.3's topic-scoped shape - does not, so a topic explicitly
    /// named in an atomic client's include-list survives the hash even if it is reserved. Internal:
    /// this is a hash-normalization detail of how the two builders divide the work, not something a
    /// caller configuring either builder chooses directly.
    /// </summary>
    internal bool IsTopicScopedForHash { get; set; }
}
