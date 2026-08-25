using System.Text.Json;

namespace Benzene.Outbox.EntityFramework;

/// <summary>
/// The EF Core row shape for a persisted <see cref="OutboxEnvelope"/>, mapped via
/// <see cref="ModelBuilderExtensions.AddOutboxEntities"/>. Lives directly in the application's own
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> - that is the whole point (see
/// <c>Benzene.Outbox.EntityFramework/CLAUDE.md</c>'s same-<c>DbContext</c> atomicity story).
/// </summary>
/// <remarks>
/// Carries two columns <see cref="OutboxEnvelope"/> doesn't have: <see cref="LeaseUntil"/> (the claim
/// lease <see cref="EntityFrameworkOutboxStore{TDbContext}"/> uses for atomic claiming) and
/// <see cref="DispatchedAtUtc"/> (the retention cutoff for <c>DeleteDispatchedBeforeAsync</c>, since a
/// relational store has no native TTL). <see cref="RowVersion"/> is a hand-rolled concurrency token
/// (bumped on every write via <see cref="Touch"/>) used only by the optimistic-concurrency fallback
/// claim path for providers that don't support <c>ExecuteUpdateAsync</c> - see
/// <see cref="EntityFrameworkOutboxStore{TDbContext}"/>'s remarks.
/// </remarks>
public class OutboxRecord
{
    /// <summary>The envelope's id (see <see cref="OutboxEnvelope.Id"/>). Primary key.</summary>
    public string Id { get; set; } = default!;

    /// <summary>See <see cref="OutboxEnvelope.Topic"/>.</summary>
    public string Topic { get; set; } = default!;

    /// <summary>See <see cref="OutboxEnvelope.Payload"/>.</summary>
    public string Payload { get; set; } = default!;

    /// <summary>See <see cref="OutboxEnvelope.PayloadType"/>.</summary>
    public string PayloadType { get; set; } = default!;

    /// <summary>
    /// <see cref="OutboxEnvelope.Headers"/>, serialized as JSON - EF Core has no built-in mapping for
    /// an arbitrary string dictionary, and this package takes no dependency on a particular
    /// database's native JSON column type to stay provider-neutral.
    /// </summary>
    public string HeadersJson { get; set; } = "{}";

    /// <summary>See <see cref="OutboxEnvelope.CreatedAtUtc"/>.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>See <see cref="OutboxEnvelope.AttemptCount"/>.</summary>
    public int AttemptCount { get; set; }

    /// <summary>See <see cref="OutboxEnvelope.NextAttemptAtUtc"/>. Part of the sweep index (see <see cref="ModelBuilderExtensions.AddOutboxEntities"/>).</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    /// <summary>See <see cref="OutboxEnvelope.Status"/>. Part of the sweep index.</summary>
    public OutboxStatus Status { get; set; }

    /// <summary>See <see cref="OutboxEnvelope.LastError"/>.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// When a claim on this row expires, or <see langword="null"/> if unclaimed. Not part of
    /// <see cref="OutboxEnvelope"/> - a store-internal claim/lease detail.
    /// </summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>
    /// The opaque claim-fencing token minted on the current lease, or <see langword="null"/> if
    /// unclaimed. Mirrors <see cref="OutboxEnvelope.LeaseToken"/>; <see cref="EntityFrameworkOutboxStore{TDbContext}"/>'s
    /// settle methods require this back and condition their write on it still matching (see that
    /// type's remarks).
    /// </summary>
    public string? LeaseToken { get; set; }

    /// <summary>
    /// When this row was dispatched, or <see langword="null"/> if it never has been. Drives
    /// <c>DeleteDispatchedBeforeAsync</c>'s retention cutoff.
    /// </summary>
    public DateTimeOffset? DispatchedAtUtc { get; set; }

    /// <summary>
    /// A concurrency token, re-randomized on every write via <see cref="Touch"/>. Only consulted by
    /// the optimistic-concurrency fallback claim path (see class remarks); harmless on providers that
    /// never take that path.
    /// </summary>
    public string RowVersion { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Builds the row for a freshly captured envelope (unclaimed, undispatched).</summary>
    public static OutboxRecord FromEnvelope(OutboxEnvelope envelope) => new()
    {
        Id = envelope.Id,
        Topic = envelope.Topic,
        Payload = envelope.Payload,
        PayloadType = envelope.PayloadType,
        HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>(envelope.Headers)),
        CreatedAtUtc = envelope.CreatedAtUtc,
        AttemptCount = envelope.AttemptCount,
        NextAttemptAtUtc = envelope.NextAttemptAtUtc,
        Status = envelope.Status,
        LastError = envelope.LastError,
        LeaseUntil = null,
        LeaseToken = null,
        DispatchedAtUtc = null,
        RowVersion = Guid.NewGuid().ToString("N"),
    };

    /// <summary>
    /// Rehydrates the immutable <see cref="OutboxEnvelope"/> this row currently represents, including
    /// stamping <see cref="OutboxEnvelope.LeaseToken"/> from <see cref="LeaseToken"/> - callers that
    /// use this outside a claim (e.g. reading an unclaimed row) will just see it as
    /// <see langword="null"/>.
    /// </summary>
    public OutboxEnvelope ToEnvelope()
    {
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(HeadersJson) ?? new Dictionary<string, string>();
        return new OutboxEnvelope(Id, Topic, Payload, PayloadType, headers, CreatedAtUtc, AttemptCount, NextAttemptAtUtc, Status, LastError, LeaseToken);
    }

    /// <summary>Whether this row is <see cref="OutboxStatus.Pending"/>, due, and not currently leased - the claim predicate.</summary>
    public bool IsDue(DateTimeOffset now) =>
        Status == OutboxStatus.Pending
        && (NextAttemptAtUtc == null || NextAttemptAtUtc <= now)
        && (LeaseUntil == null || LeaseUntil <= now);

    /// <summary>Re-randomizes <see cref="RowVersion"/> - call before every <c>SaveChanges</c> that mutates this row.</summary>
    public void Touch() => RowVersion = Guid.NewGuid().ToString("N");
}
