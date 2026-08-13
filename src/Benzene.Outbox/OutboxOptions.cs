namespace Benzene.Outbox;

/// <summary>
/// Configuration for the outbox: both the per-route capture behavior (<see cref="WriteMode"/>,
/// <see cref="StampIdempotencyKey"/>, set via <c>AddOutbox(configure)</c> and optionally overridden
/// per route via <c>UseOutbox(configure)</c>) and the process-wide dispatch engine behavior
/// (everything else, set via <c>AddOutbox(configure)</c> only).
/// </summary>
/// <remarks>
/// <see cref="Extensions.AddOutbox"/> registers the one shared <see cref="OutboxOptions"/> instance
/// that both the dispatch engine and every route's default capture behavior read - so
/// <c>AddOutbox(o => o.WriteMode = OutboxWriteMode.Transactional)</c> alone makes every subsequent
/// bare <c>UseOutbox()</c> call capture transactionally, with no need to repeat the setting per
/// route. <see cref="Extensions.UseOutbox"/> starts from a **clone** of that shared instance (never
/// the shared instance itself, so one route's override can never bleed into another's) and applies
/// its own <c>configure</c> delegate on top - so a route that wants to diverge from the process-wide
/// default still can, via its own <c>UseOutbox(o => o.WriteMode = ...)</c> call.
/// </remarks>
public class OutboxOptions
{
    /// <summary>
    /// How many times a dispatch is retried before the envelope is parked. Defaults to <c>10</c>.
    /// Consumed by the dispatch engine (configure via <c>AddOutbox</c>).
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// The base delay for exponential backoff between retry attempts (attempt 1's delay). Defaults to
    /// 30 seconds. Consumed by the dispatch engine (configure via <c>AddOutbox</c>).
    /// </summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The maximum delay a retry's exponential backoff can grow to. Defaults to 1 hour. Consumed by
    /// the dispatch engine (configure via <c>AddOutbox</c>).
    /// </summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a <see cref="OutboxStatus.Dispatched"/> envelope is retained before
    /// <see cref="IOutboxStore.DeleteDispatchedBeforeAsync"/> removes it. Defaults to 7 days.
    /// <see cref="OutboxStatus.Parked"/> envelopes are never auto-deleted regardless of this setting.
    /// Consumed by the dispatch engine (configure via <c>AddOutbox</c>).
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The maximum number of envelopes <see cref="IOutboxDispatcher.RunOnceAsync"/> claims in one
    /// run. Defaults to <c>25</c>. Consumed by the dispatch engine (configure via <c>AddOutbox</c>).
    /// </summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>
    /// How long a claimed envelope's lease is held before another claimer may take it over (the
    /// crash-recovery window). Defaults to 2 minutes. Consumed by the dispatch engine (configure via
    /// <c>AddOutbox</c>).
    /// </summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often <see cref="OutboxDispatcherWorker"/>'s poll loop calls
    /// <see cref="IOutboxDispatcher.RunOnceAsync"/>. Defaults to 5 seconds. Consumed by the dispatch
    /// engine (configure via <c>AddOutbox</c>).
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When an envelope's headers lack <c>idempotency-key</c> (<see cref="OutboxDefaults.IdempotencyKeyHeaderName"/>),
    /// whether <see cref="OutboxMiddleware"/> stamps the envelope's own id into that header at capture
    /// time. Defaults to <c>true</c> - a consumer running <c>Benzene.Idempotency</c>'s
    /// <c>UseIdempotency()</c> with the default key strategy then dedups relay redeliveries with zero
    /// extra configuration. Consumed at capture time (configure via <c>UseOutbox</c>).
    /// </summary>
    public bool StampIdempotencyKey { get; set; } = true;

    /// <summary>
    /// How <see cref="OutboxMiddleware"/> captures an envelope for this route. Defaults to
    /// <see cref="OutboxWriteMode.Immediate"/>. Consumed at capture time (configure via
    /// <c>UseOutbox</c>).
    /// </summary>
    public OutboxWriteMode WriteMode { get; set; } = OutboxWriteMode.Immediate;

    /// <summary>Returns a shallow copy - used by <c>UseOutbox</c> to derive a per-route options
    /// instance from the shared one <c>AddOutbox</c> registered, without ever mutating it.</summary>
    internal OutboxOptions Clone() => new()
    {
        MaxAttempts = MaxAttempts,
        BackoffBase = BackoffBase,
        BackoffCap = BackoffCap,
        RetentionPeriod = RetentionPeriod,
        BatchSize = BatchSize,
        ClaimLease = ClaimLease,
        PollInterval = PollInterval,
        StampIdempotencyKey = StampIdempotencyKey,
        WriteMode = WriteMode,
    };
}
