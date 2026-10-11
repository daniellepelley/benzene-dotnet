namespace Benzene.CodeGen.Client;

/// <summary>
/// Configuration for <see cref="MessageClientSdkBuilder"/> and <see cref="AtomicClientSdkBuilder"/>:
/// naming (service name, generated namespace) and topic scoping (an include-list, contract-document.md
/// §5.2). The reserved-topic option is gone: a contract document lists no <c>benzene:</c> topic. Both builders' legacy positional constructors delegate to an options-based
/// constructor built from an instance of this class with today's defaults, so existing call sites
/// and golden-file tests keep behaving identically - see
/// work/archive/spec-mesh-tooling-implementation-plan-2026-08.md Phase 3b step 1.
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
    /// The topic include-list: only these topics are in scope. Null or empty means "every topic" (a
    /// contract document lists no <c>benzene:</c> topic, contract-document.md §1). A topic named here that the document does not
    /// have fails loud rather than silently generating nothing for it.
    /// </summary>
    public IReadOnlyCollection<string>? Topics { get; set; }

}
