namespace Benzene.Schema.OpenApi.EventService;

/// <summary>
/// Where a service runs - the contract document's optional <c>placement</c> (contract-document.md
/// §1). <see cref="Cloud"/> is detected from the platform's documented environment or configured
/// (<c>aws</c>, <c>azure</c>, <c>gcp</c>, <c>self-hosted</c>, or an explicit override);
/// <see cref="Region"/> is set only where the platform documents a way to know it - never guessed.
/// </summary>
public sealed class ContractDocumentPlacement
{
    /// <summary>Initializes a placement.</summary>
    /// <param name="cloud">The cloud.</param>
    /// <param name="region">The region, or null when the platform documents no way to know it.</param>
    public ContractDocumentPlacement(string cloud, string? region = null)
    {
        Cloud = cloud;
        Region = string.IsNullOrWhiteSpace(region) ? null : region;
    }

    /// <summary>The cloud.</summary>
    public string Cloud { get; }

    /// <summary>The region, or null.</summary>
    public string? Region { get; }
}

/// <summary>
/// A conformance-profile self-assessment - the contract document's optional <c>profile</c>
/// (contract-document.md §1): <see cref="Name"/> (<c>cloud-service</c> for the Cloud Service Profile)
/// and <see cref="Missing"/>, the requirement ids the service's own wiring knows it does not meet. It
/// reflects wiring, not runtime health.
/// </summary>
public sealed class ContractDocumentProfile
{
    /// <summary>Initializes a profile claim.</summary>
    /// <param name="name">The profile's name.</param>
    /// <param name="missing">The requirement ids not met; null or empty when none.</param>
    public ContractDocumentProfile(string name, IReadOnlyList<string>? missing = null)
    {
        Name = name;
        Missing = missing is { Count: > 0 } ? missing.ToArray() : Array.Empty<string>();
    }

    /// <summary>The profile's name.</summary>
    public string Name { get; }

    /// <summary>The requirement ids the service's wiring does not meet; empty when none (and then omitted on the wire).</summary>
    public IReadOnlyList<string> Missing { get; }
}

/// <summary>
/// The deployment-side fields a service knows about itself - <c>runtime</c>, <c>placement</c> and
/// <c>profile</c> (contract-document.md §1). They describe where and how the service runs rather than
/// what it serves, so they are outside both hashes (§6.2). Register one with
/// <c>SetContractDocumentDeployment</c> and every contract document <see cref="SpecBuilder"/> derives
/// carries it - the document <c>benzene:spec</c> serves and the <c>benzene:mesh:register</c> body
/// alike, which are the same document (mesh.md §2).
/// </summary>
public sealed class ContractDocumentDeployment
{
    /// <summary>This port's <c>runtime</c> value.</summary>
    public const string DotNetRuntime = "dotnet";

    /// <summary>The implementing port; <see cref="DotNetRuntime"/> unless overridden.</summary>
    public string? Runtime { get; init; } = DotNetRuntime;

    /// <summary>Where the service runs, or null when unknown.</summary>
    public ContractDocumentPlacement? Placement { get; init; }

    /// <summary>The service's profile self-assessment, or null when it makes none.</summary>
    public ContractDocumentProfile? Profile { get; init; }
}

/// <summary>
/// The members of a contract document a producer may be unable to determine - the values of its
/// optional <c>degraded</c> list (contract-document.md §1). A member named in <c>degraded</c> is still
/// present, and empty: its empty list then asserts nothing, where without <c>degraded</c> it asserts
/// "none".
/// </summary>
public static class ContractDocumentMembers
{
    /// <summary>The topics the service serves.</summary>
    public const string Requests = "requests";

    /// <summary>The events the service publishes.</summary>
    public const string Events = "events";

    /// <summary>The messages the service sends to topics others define.</summary>
    public const string Sends = "sends";
}
