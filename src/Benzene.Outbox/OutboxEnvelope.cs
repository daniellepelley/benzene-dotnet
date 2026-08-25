namespace Benzene.Outbox;

/// <summary>
/// A single deferred send captured by <see cref="OutboxMiddleware"/>: the route, the serialized
/// request, and enough retry/lifecycle bookkeeping for <see cref="IOutboxDispatcher"/> to eventually
/// deliver it through the same outbound route pipeline.
/// </summary>
/// <remarks>
/// Immutable snapshot type - an <see cref="IOutboxStore"/> hands out a fresh instance reflecting
/// whatever it currently has on record; it does not hand out a live, mutable reference callers can
/// poke. <see cref="Payload"/> must round-trip through the registered <c>ISerializer</c> (deserialize
/// to <see cref="PayloadType"/>, re-serialize at dispatch) - the same constraint
/// <c>Benzene.EventSourcing</c> already imposes on stored events. Dispatch runs in the same service,
/// so the payload type is normally always loadable; a renamed payload type strands pending envelopes
/// down the park path (see <c>Benzene.Outbox/CLAUDE.md</c>).
/// </remarks>
public sealed class OutboxEnvelope
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxEnvelope"/> class.
    /// </summary>
    /// <param name="id">
    /// The envelope's id (a <see cref="Guid"/> in <c>"N"</c> format by convention). Also the default
    /// <c>idempotency-key</c> value stamped by <see cref="OutboxMiddleware"/> when
    /// <see cref="OutboxOptions.StampIdempotencyKey"/> is enabled and the captured headers don't
    /// already carry one.
    /// </param>
    /// <param name="topic">The topic this envelope will eventually be sent on.</param>
    /// <param name="payload">The request, serialized by the capturing route scope's <c>ISerializer</c>.</param>
    /// <param name="payloadType">The assembly-qualified name of the request's runtime type, for dispatch-time deserialization.</param>
    /// <param name="headers">
    /// The post-stamping header snapshot captured at the moment <see cref="OutboxMiddleware"/> ran -
    /// so a relay replays the original business-time <c>traceparent</c>/<c>x-correlation-id</c>
    /// instead of the relay host's own ambient context.
    /// </param>
    /// <param name="createdAtUtc">When the envelope was captured.</param>
    /// <param name="attemptCount">How many dispatch attempts have been made so far. Defaults to <c>0</c>.</param>
    /// <param name="nextAttemptAtUtc">
    /// When the envelope next becomes due for a claim, or <see langword="null"/> if it is due
    /// immediately (a fresh capture) or no longer applicable (dispatched/parked).
    /// </param>
    /// <param name="status">The envelope's lifecycle state. Defaults to <see cref="OutboxStatus.Pending"/>.</param>
    /// <param name="lastError">A description of the most recent dispatch failure, or <see langword="null"/> if none has occurred yet.</param>
    /// <param name="leaseToken">
    /// The opaque claim-fencing token stamped by an <see cref="IOutboxStore"/> on a successful
    /// <see cref="IOutboxStore.ClaimDueAsync"/>/<see cref="IOutboxStore.ClaimAsync"/>, or
    /// <see langword="null"/> for an envelope that hasn't just been claimed (a fresh capture, or one
    /// rehydrated outside the claim path). See <see cref="LeaseToken"/>.
    /// </param>
    public OutboxEnvelope(
        string id,
        string topic,
        string payload,
        string payloadType,
        IReadOnlyDictionary<string, string> headers,
        DateTimeOffset createdAtUtc,
        int attemptCount = 0,
        DateTimeOffset? nextAttemptAtUtc = null,
        OutboxStatus status = OutboxStatus.Pending,
        string? lastError = null,
        string? leaseToken = null)
    {
        Id = id;
        Topic = topic;
        Payload = payload;
        PayloadType = payloadType;
        Headers = headers;
        CreatedAtUtc = createdAtUtc;
        AttemptCount = attemptCount;
        NextAttemptAtUtc = nextAttemptAtUtc;
        Status = status;
        LastError = lastError;
        LeaseToken = leaseToken;
    }

    /// <summary>Gets the envelope's id (a <see cref="Guid"/> in <c>"N"</c> format by convention).</summary>
    public string Id { get; }

    /// <summary>Gets the topic this envelope will eventually be sent on.</summary>
    public string Topic { get; }

    /// <summary>Gets the request, serialized by the capturing route scope's <c>ISerializer</c>.</summary>
    public string Payload { get; }

    /// <summary>Gets the assembly-qualified name of the request's runtime type.</summary>
    public string PayloadType { get; }

    /// <summary>Gets the post-stamping header snapshot captured at capture time.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Gets when the envelope was captured.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets how many dispatch attempts have been made so far.</summary>
    public int AttemptCount { get; }

    /// <summary>Gets when the envelope next becomes due for a claim, or <see langword="null"/>.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; }

    /// <summary>Gets the envelope's lifecycle state.</summary>
    public OutboxStatus Status { get; }

    /// <summary>Gets a description of the most recent dispatch failure, or <see langword="null"/>.</summary>
    public string? LastError { get; }

    /// <summary>
    /// Gets the opaque claim-fencing token stamped by the <see cref="IOutboxStore"/> that most
    /// recently claimed this envelope, or <see langword="null"/> if it hasn't just been claimed.
    /// Rotated on every successful claim. The dispatcher MUST present this token, unchanged, to the
    /// settle call (<see cref="IOutboxStore.MarkDispatchedAsync"/>/<see cref="IOutboxStore.RescheduleAsync"/>/
    /// <see cref="IOutboxStore.ParkAsync"/>) - a settle whose token no longer matches the live lease
    /// (it lapsed and was reclaimed by another worker) is refused rather than allowed to clobber
    /// whoever holds the lease now.
    /// </summary>
    public string? LeaseToken { get; }

    /// <summary>
    /// Returns a copy of this envelope stamped with <paramref name="leaseToken"/>. Used by
    /// <see cref="IOutboxStore"/> implementations immediately after a successful claim to hand the
    /// caller the fencing token alongside the envelope, without repeating the full constructor call at
    /// every claim site.
    /// </summary>
    /// <param name="leaseToken">The token to stamp (see <see cref="LeaseToken"/>).</param>
    public OutboxEnvelope WithLeaseToken(string? leaseToken) =>
        new(Id, Topic, Payload, PayloadType, Headers, CreatedAtUtc, AttemptCount, NextAttemptAtUtc, Status, LastError, leaseToken);
}
