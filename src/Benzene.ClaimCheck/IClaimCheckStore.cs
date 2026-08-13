namespace Benzene.ClaimCheck;

/// <summary>
/// Pluggable persistence for claim-checked payloads. The outbound offload middleware
/// (<see cref="ClaimCheckOffloadMiddleware"/>) puts an oversized serialized body and gets back an
/// opaque reference; the inbound hydrate middleware (<see cref="ClaimCheckHydrateMiddleware{TContext}"/>)
/// resolves that reference back to the body before deserialization. Swap the implementation to
/// change where offloaded payloads live (in-memory for tests/local dev, S3/Azure Blob Storage in
/// production) without touching either middleware.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Retention is the store's own responsibility</strong> (a time-to-live / lifecycle rule),
/// not the middleware's - there is no delete-on-consume. A fan-out transport (SNS) can deliver the
/// same reference to several independent consumers, and an at-least-once transport can redeliver the
/// same message, so the first successful hydration must never remove a payload later reads still
/// need. Size the retention window to outlive the longest possible path from send to last possible
/// consumption (queue retention plus any DLQ redrive window).
/// </para>
/// <para>
/// <strong>A store MUST refuse a reference outside its own configuration</strong> - the wrong
/// scheme, or (for a cloud store) a bucket/container/prefix it was not configured for - rather than
/// attempt to fetch it. Never resolve an attacker-supplied or otherwise foreign location.
/// </para>
/// </remarks>
public interface IClaimCheckStore
{
    /// <summary>
    /// Stores <paramref name="body"/> - the serialized wire body that was too large to send inline -
    /// and returns an opaque, store-issued reference in URI form (<c>scheme://location/key</c>, e.g.
    /// <c>s3://my-bucket/claim-checks/orders/2026/08/13/1a2b….json</c>) that <see cref="GetAsync"/>
    /// can later resolve. The reference is what travels on the wire, in the header named by
    /// <see cref="ClaimCheckOptions.HeaderName"/> (default <see cref="ClaimCheckHeaders.ClaimCheck"/>).
    /// </summary>
    /// <param name="body">The serialized wire body to store, verbatim.</param>
    /// <param name="context">Context about the message being offloaded (its topic), for key
    /// partitioning.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The store-issued reference.</returns>
    Task<string> PutAsync(string body, ClaimCheckPutContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a previously-issued <paramref name="reference"/> back to its stored body.
    /// </summary>
    /// <param name="reference">The reference previously returned by <see cref="PutAsync"/> (read
    /// from the wire header).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The stored body, verbatim; or <c>null</c> when <paramref name="reference"/> is well-formed for
    /// this store but not found or has expired. The hydrate middleware turns a <c>null</c> into a
    /// loud <see cref="ClaimCheckNotFoundException"/> so the transport's normal failure semantics
    /// (nack, redelivery, DLQ) apply - never a silent skip.
    /// </returns>
    /// <exception cref="ClaimCheckStoreMismatchException">
    /// <paramref name="reference"/> lies outside this store's own configuration (wrong scheme, or for
    /// a cloud store, a bucket/container/prefix it was not configured for). Implementations MUST
    /// throw here rather than return <c>null</c> or attempt the fetch anyway.
    /// </exception>
    Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default);
}
