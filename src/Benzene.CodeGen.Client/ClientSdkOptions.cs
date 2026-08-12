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
    /// The topic include-list: only these topics (plus the always-exempt
    /// <c>benzene:healthcheck</c>) are in scope. Null or empty means "every topic", subject to
    /// <see cref="IncludeReservedTopics"/>. A topic named here that the document does not have
    /// fails loud rather than silently generating nothing for it.
    /// </summary>
    public IReadOnlyCollection<string>? Topics { get; set; }

    /// <summary>
    /// When false (the default) and <see cref="Topics"/> is not set, reserved Benzene utility
    /// topics (<c>benzene:spec</c>, <c>benzene:mesh</c>, ... - see
    /// <see cref="Benzene.Schema.OpenApi.ReservedTopics"/>) other than the always-exempt healthcheck
    /// topic are excluded, so a generated client only covers a service's domain surface by default.
    /// Ignored when <see cref="Topics"/> explicitly names a topic: an explicit ask always wins over
    /// this default.
    /// </summary>
    public bool IncludeReservedTopics { get; set; }
}
