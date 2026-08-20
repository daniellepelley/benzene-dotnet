using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Benzene.ClaimCheck.Azure.Blob;

/// <summary>
/// An <see cref="IClaimCheckStore"/> backed by an Azure Blob Storage container - the Azure analogue of
/// <c>Benzene.ClaimCheck.Aws.S3.S3ClaimCheckStore</c>. Issues opaque references of the form
/// <c>azblob://{container}/{key}</c>; the key layout is <c>{prefix}{topic}/{yyyy/MM/dd}/{guid}</c>
/// (topic verbatim, a UTC date segment so the container's lifecycle rule's effect is auditable by
/// browsing the container). See <c>work/archive/claim-check-plan-2026-08.md</c> §2-§3 for why this is a separate store
/// from <c>Benzene.Mesh.Azure.Blob.BlobMeshArtifactStore</c> and for the retention/failure posture this
/// type honors (no delete-on-consume; TTL-based expiry owned by a Blob lifecycle-management policy on
/// the container/prefix; a missing/expired blob resolves to <c>null</c>, never a silent substitute).
/// </summary>
public class BlobClaimCheckStore : IClaimCheckStore
{
    /// <summary>The reference scheme this store issues and accepts: <c>azblob</c>.</summary>
    public const string Scheme = "azblob";

    private readonly BlobContainerClient _container;
    private readonly string _prefix;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobClaimCheckStore"/> class.
    /// </summary>
    /// <param name="container">
    /// The container offloaded payloads are written to and read from. The store does not create the
    /// container - it must already exist, provisioned with a lifecycle-management delete rule scoped
    /// to <paramref name="prefix"/> (see the package's <c>CLAUDE.md</c> for the TTL sizing rule).
    /// </param>
    /// <param name="prefix">
    /// A blob-name prefix all keys this store issues/accepts live under. Defaults to
    /// <c>"claim-checks/"</c>. Normalized to end with exactly one trailing slash.
    /// </param>
    /// <param name="now">A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public BlobClaimCheckStore(BlobContainerClient container, string prefix = "claim-checks/", Func<DateTimeOffset>? now = null)
    {
        _container = container;
        _prefix = string.IsNullOrEmpty(prefix) ? "" : prefix.TrimEnd('/') + "/";
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<string> PutAsync(string body, ClaimCheckPutContext context, CancellationToken cancellationToken = default)
    {
        var key = $"{_prefix}{context.Topic}/{_now():yyyy/MM/dd}/{Guid.NewGuid():n}";
        var blob = _container.GetBlobClient(key);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" }
            },
            cancellationToken);

        return $"{Scheme}://{_container.Name}/{key}";
    }

    /// <inheritdoc />
    /// <exception cref="ClaimCheckStoreMismatchException">
    /// <paramref name="reference"/> does not use the <see cref="Scheme"/> this store issues, or names a
    /// container or blob-name prefix other than this store's own configuration.
    /// </exception>
    public async Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        var key = ValidateAndExtractKey(reference);
        var blob = _container.GetBlobClient(key);

        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private string ValidateAndExtractKey(string reference)
    {
        var expectedReferencePrefix = $"{Scheme}://{_container.Name}/";
        if (string.IsNullOrEmpty(reference) || !reference.StartsWith(expectedReferencePrefix, StringComparison.Ordinal))
        {
            // Wrong scheme, or a container other than this store's own - refuse rather than fetch.
            throw new ClaimCheckStoreMismatchException(reference);
        }

        var key = reference[expectedReferencePrefix.Length..];
        if (!key.StartsWith(_prefix, StringComparison.Ordinal))
        {
            // Right container, but outside this store's configured prefix - still a foreign reference.
            throw new ClaimCheckStoreMismatchException(reference);
        }

        return key;
    }
}
