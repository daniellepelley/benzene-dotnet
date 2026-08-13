namespace Benzene.Outbox;

/// <summary>
/// Configuration for the outbox: both the per-route capture behavior (<see cref="WriteMode"/>,
/// <see cref="StampIdempotencyKey"/>, set via <c>UseOutbox(configure)</c>) and the process-wide
/// dispatch engine behavior (everything else, set via <c>AddOutbox(configure)</c>).
/// </summary>
/// <remarks>
/// <see cref="Extensions.UseOutbox"/> and <see cref="Extensions.AddOutbox"/> each build their own,
/// independent <see cref="OutboxOptions"/> instance - mirroring how <c>Benzene.Idempotency</c>'s
/// <c>UseIdempotency</c> builds its own <c>IdempotencyOptions</c> per route, unrelated to any other
/// route's. A route that wants <see cref="OutboxWriteMode.Transactional"/> must say so at its own
/// <c>UseOutbox(o => o.WriteMode = OutboxWriteMode.Transactional)</c> call even if every other route
/// uses the default - the fields set via <c>AddOutbox</c> (<see cref="MaxAttempts"/>,
/// <see cref="BackoffBase"/>, <see cref="BackoffCap"/>, <see cref="RetentionPeriod"/>,
/// <see cref="BatchSize"/>, <see cref="ClaimLease"/>, <see cref="PollInterval"/>) configure the one
/// shared <see cref="IOutboxDispatcher"/>/<see cref="OutboxDispatcherWorker"/> instead, and are
/// naturally process-wide rather than per-route.
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
}
