using Azure.Identity;
using Azure.Storage.Blobs;
using Benzene.Abstractions.DI;

namespace Benzene.ClaimCheck.Azure.Blob;

/// <summary>Registration for the Azure Blob Storage-backed <see cref="IClaimCheckStore"/>.</summary>
public static class Extensions
{
    /// <summary>
    /// Registers <see cref="BlobClaimCheckStore"/> as the <see cref="IClaimCheckStore"/>, backed by a
    /// caller-supplied <see cref="BlobContainerClient"/>. Call once at application setup, on both the
    /// sending and receiving service - a payload offloaded by one must be resolvable by whichever
    /// instance of the other receives the message, so the container (and prefix) must be shared.
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="container">
    /// The container offloaded payloads are written to/read from. Must already exist - this method
    /// does not create it. Provision a Blob lifecycle-management delete rule scoped to
    /// <paramref name="prefix"/> as the retention mechanism (see the package's <c>CLAUDE.md</c> for
    /// the TTL sizing rule).
    /// </param>
    /// <param name="prefix">An optional blob-name prefix within the container. Defaults to <c>"claim-checks/"</c>.</param>
    public static IBenzeneServiceContainer AddBlobClaimCheckStore(
        this IBenzeneServiceContainer services, BlobContainerClient container, string prefix = "claim-checks/")
    {
        services.TryAddSingleton<IClaimCheckStore>(new BlobClaimCheckStore(container, prefix));
        return services;
    }

    /// <summary>
    /// Convenience overload that builds the <see cref="BlobContainerClient"/> from a blob service URI
    /// and container name, authenticated with <see cref="DefaultAzureCredential"/> (managed identity in
    /// Azure, the developer credential locally). The identity needs the <c>Storage Blob Data
    /// Contributor</c> role on the container (or storage account) - see the package's <c>CLAUDE.md</c>
    /// for the full RBAC note.
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="blobServiceUri">The storage account's blob endpoint, e.g. <c>https://acct.blob.core.windows.net</c>.</param>
    /// <param name="containerName">The container name.</param>
    /// <param name="prefix">An optional blob-name prefix within the container. Defaults to <c>"claim-checks/"</c>.</param>
    public static IBenzeneServiceContainer AddBlobClaimCheckStore(
        this IBenzeneServiceContainer services, Uri blobServiceUri, string containerName, string prefix = "claim-checks/")
    {
        var container = new BlobServiceClient(blobServiceUri, new DefaultAzureCredential())
            .GetBlobContainerClient(containerName);
        return services.AddBlobClaimCheckStore(container, prefix);
    }
}
