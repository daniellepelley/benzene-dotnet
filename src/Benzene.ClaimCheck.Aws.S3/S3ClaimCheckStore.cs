using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Benzene.ClaimCheck;

namespace Benzene.ClaimCheck.Aws.S3;

/// <summary>
/// An <see cref="IClaimCheckStore"/> backed by an Amazon S3 bucket. Puts issue keys of the shape
/// <c>{prefix}{topic}/{yyyy/MM/dd}/{guid}</c> - the topic travels into the key verbatim (S3 keys
/// allow the colons a Benzene topic name commonly contains) and the date segment makes a bucket
/// lifecycle rule's effect on the prefix auditable by eye. Gets resolve an <c>s3://{bucket}/{key}</c>
/// reference back to its stored body, refusing (via <see cref="ClaimCheckStoreMismatchException"/>)
/// any reference whose scheme, bucket, or prefix does not belong to this store's own configuration.
/// </summary>
/// <remarks>
/// <para>
/// This store does not create the bucket, and it does not create or manage a lifecycle rule -
/// retention is the caller's infrastructure responsibility (see
/// <see cref="Extensions.AddS3ClaimCheckStore"/>'s remarks and <c>work/claim-check-plan.md</c> §3).
/// </para>
/// </remarks>
public class S3ClaimCheckStore : IClaimCheckStore
{
    /// <summary>The reference scheme this store issues and accepts: <c>s3</c>.</summary>
    public const string Scheme = "s3";

    private const string DefaultContentType = "application/octet-stream";

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;

    /// <summary>Initializes the store.</summary>
    /// <param name="s3">The S3 client. The consumer registers and owns its lifecycle.</param>
    /// <param name="bucket">The bucket offloaded payloads are written to and read from. Must already exist.</param>
    /// <param name="prefix">An optional key prefix (e.g. <c>"claim-checks/"</c>). Defaults to none.</param>
    public S3ClaimCheckStore(IAmazonS3 s3, string bucket, string prefix = "")
    {
        _s3 = s3;
        _bucket = bucket;
        _prefix = string.IsNullOrEmpty(prefix) ? "" : prefix.TrimEnd('/') + "/";
    }

    /// <inheritdoc />
    public async Task<string> PutAsync(string body, ClaimCheckPutContext context, CancellationToken cancellationToken = default)
    {
        var key = Key(context.Topic);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = body,
            ContentType = DefaultContentType
        }, cancellationToken);

        return $"{Scheme}://{_bucket}/{key}";
    }

    /// <inheritdoc />
    /// <exception cref="ClaimCheckStoreMismatchException">
    /// <paramref name="reference"/> does not use the <see cref="Scheme"/> this store issues, or names
    /// a bucket/prefix this store was not configured for.
    /// </exception>
    public async Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        var key = ParseAndValidate(reference);

        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private string Key(string topic) => $"{_prefix}{topic}/{DateTimeOffset.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():n}";

    // A single prefix comparison covers all three things a foreign reference could get wrong at
    // once - scheme, bucket, and prefix - rather than parsing the reference into a Uri: this store's
    // own key prefixes can legitimately contain characters (e.g. the ':' in a Benzene topic name)
    // that would otherwise need escaping/unescaping to round-trip through System.Uri unchanged.
    private string ParseAndValidate(string reference)
    {
        var basePrefix = $"{Scheme}://{_bucket}/";
        var configuredPrefix = basePrefix + _prefix;

        if (!reference.StartsWith(configuredPrefix, StringComparison.Ordinal))
        {
            throw new ClaimCheckStoreMismatchException(reference);
        }

        return reference[basePrefix.Length..];
    }
}
