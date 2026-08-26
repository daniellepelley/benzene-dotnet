using Benzene.HealthChecks.Core;

namespace Benzene.Mesh.Contracts;

/// <summary>
/// The full per-service artifact a <c>Benzene.Mesh.Aggregator</c> publishes on each run - the
/// <c>services/{name}.json</c> shape.
/// </summary>
public class MeshServiceSnapshot
{
    /// <summary>Initializes a new instance of the <see cref="MeshServiceSnapshot"/> class.</summary>
    /// <param name="name">The service's name (matches the registry entry's <c>Name</c>).</param>
    /// <param name="fetchedAtUtc">When this snapshot was taken.</param>
    /// <param name="specJson">The raw spec document JSON, verbatim from the service's spec endpoint - not deserialized. <c>null</c> if the spec could not be fetched.</param>
    /// <param name="specHash">The hash of <paramref name="specJson"/> (see <see cref="MeshHashing"/>). <c>null</c> if the spec could not be fetched.</param>
    /// <param name="previousSpecHash">The <see cref="SpecHash"/> from the previous run, if any.</param>
    /// <param name="contractDrift">Whether <see cref="SpecHash"/> differs from <see cref="PreviousSpecHash"/>. Always <c>false</c> the first time a service is seen.</param>
    /// <param name="health">The service's aggregated health check response. <c>null</c> if the health endpoint could not be reached.</param>
    /// <param name="error">The type name of the exception thrown while fetching the spec and/or health endpoint, if any - deliberately never the exception message, since this artifact aggregates across services into something with broader visibility than one service's own health endpoint.</param>
    /// <param name="errorClass">
    /// One of <see cref="MeshErrorClass"/> - what <em>kind</em> of failure <paramref name="error"/> was
    /// (permission/unreachable/timeout/other), derived from the underlying SDK exception's status-code
    /// shape where one is present. <c>null</c> when there was no error, or the shape wasn't recognised.
    /// Additive/optional - a snapshot written before this field existed deserializes with it <c>null</c>.
    /// </param>
    public MeshServiceSnapshot(
        string name,
        DateTimeOffset fetchedAtUtc,
        string? specJson,
        string? specHash,
        string? previousSpecHash,
        bool contractDrift,
        HealthCheckResponse? health,
        string? error,
        string? errorClass = null)
    {
        Name = name;
        FetchedAtUtc = fetchedAtUtc;
        SpecJson = specJson;
        SpecHash = specHash;
        PreviousSpecHash = previousSpecHash;
        ContractDrift = contractDrift;
        Health = health;
        Error = error;
        ErrorClass = errorClass;
    }

    /// <summary>The service's name (matches the registry entry's <c>Name</c>).</summary>
    public string Name { get; }

    /// <summary>When this snapshot was taken.</summary>
    public DateTimeOffset FetchedAtUtc { get; }

    /// <summary>The raw spec document JSON, verbatim from the service's spec endpoint - not deserialized. <c>null</c> if the spec could not be fetched.</summary>
    public string? SpecJson { get; }

    /// <summary>The hash of <see cref="SpecJson"/> (see <see cref="MeshHashing"/>). <c>null</c> if the spec could not be fetched.</summary>
    public string? SpecHash { get; }

    /// <summary>The <see cref="SpecHash"/> from the previous run, if any.</summary>
    public string? PreviousSpecHash { get; }

    /// <summary>Whether <see cref="SpecHash"/> differs from <see cref="PreviousSpecHash"/>. Always <c>false</c> the first time a service is seen.</summary>
    public bool ContractDrift { get; }

    /// <summary>The service's aggregated health check response. <c>null</c> if the health endpoint could not be reached.</summary>
    public HealthCheckResponse? Health { get; }

    /// <summary>The type name of the exception thrown while fetching the spec and/or health endpoint, if any - never the exception message.</summary>
    public string? Error { get; }

    /// <summary>
    /// One of <see cref="MeshErrorClass"/> - what kind of failure <see cref="Error"/> was, independent
    /// of which cloud SDK threw it. <c>null</c> when there was no error, or its shape wasn't recognised.
    /// </summary>
    public string? ErrorClass { get; }
}
