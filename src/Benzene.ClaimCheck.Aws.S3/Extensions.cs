using Amazon.S3;
using Benzene.Abstractions.DI;
using Benzene.ClaimCheck;

namespace Benzene.ClaimCheck.Aws.S3;

/// <summary>
/// Registration helpers for the S3-backed <see cref="IClaimCheckStore"/>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// The default key prefix within the bucket: <c>claim-checks/</c>.
    /// </summary>
    public const string DefaultPrefix = "claim-checks/";

    /// <summary>
    /// Registers an <see cref="S3ClaimCheckStore"/> as the <see cref="IClaimCheckStore"/>, resolving
    /// <c>IAmazonS3</c> from DI - the consumer registers the client (and its credentials/region), the
    /// same posture as <c>AddDynamoDbIdempotencyStore</c>. This store never creates the bucket.
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <param name="bucket">
    /// The S3 bucket offloaded payloads are written to and read from. Must already exist and be
    /// reachable by the registered <c>IAmazonS3</c> client's credentials - this package does not
    /// create it.
    /// </param>
    /// <param name="prefix">
    /// An optional key prefix within the bucket. Defaults to <see cref="DefaultPrefix"/>.
    /// </param>
    /// <returns>The service container, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Retention is an S3 lifecycle rule you provision, not this package.</strong> Add an
    /// <c>aws_s3_bucket_lifecycle_configuration</c> (or console-equivalent) expiration rule scoped to
    /// <paramref name="prefix"/> so offloaded payloads self-delete - there is no delete-on-consume
    /// (see <see cref="IClaimCheckStore"/>'s remarks: SNS-style fan-out and at-least-once redelivery
    /// both make deleting at read time unsafe). Size the rule's expiration to exceed the longest
    /// possible path from send to last possible consumption - queue retention plus any DLQ redrive
    /// window (SQS retention maxes at 14 days; a rule of 14 days is a reasonable starting point for an
    /// SQS-fronted consumer and should be stated explicitly wherever it is configured).
    /// </para>
    /// <para>
    /// <strong>Encryption at rest defers to the bucket's own default settings</strong> (SSE-S3 or
    /// SSE-KMS) - this package does not configure or manage encryption keys. Access control is
    /// whatever IAM policy governs <paramref name="bucket"/>.
    /// </para>
    /// </remarks>
    public static IBenzeneServiceContainer AddS3ClaimCheckStore(
        this IBenzeneServiceContainer services,
        string bucket,
        string prefix = DefaultPrefix)
    {
        services.TryAddSingleton<IClaimCheckStore>(resolver =>
            new S3ClaimCheckStore(resolver.GetService<IAmazonS3>(), bucket, prefix));
        return services;
    }
}
