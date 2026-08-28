using Benzene.Abstractions.DI;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Outbox;

/// <summary>
/// The default <see cref="IOutboxDispatcher"/>. Dispatching an envelope creates a fresh DI scope (the
/// codebase's per-message pattern via <see cref="IServiceResolverFactory.CreateScope"/>), marks that
/// scope's <see cref="OutboxDispatchScope"/> so <see cref="OutboxMiddleware"/> passes the send
/// through instead of re-capturing it, deserializes the envelope's payload, and re-sends it through
/// <c>IBenzeneMessageSender</c> - the same route pipeline (transport, retry, health checks) an
/// inline send would have used.
/// </summary>
public class OutboxDispatcher : IOutboxDispatcher
{
    private readonly IOutboxStore _store;
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly OutboxOptions _options;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxDispatcher"/> class.
    /// </summary>
    /// <param name="store">The store envelopes are claimed from and reported back to.</param>
    /// <param name="serviceResolverFactory">Creates the per-envelope dispatch scope.</param>
    /// <param name="options">The dispatch engine's process-wide configuration (see <see cref="OutboxOptions"/> remarks).</param>
    /// <param name="logger">The logger used to record retry/park decisions. Defaults to <see cref="NullLogger.Instance"/>.</param>
    /// <param name="now">A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public OutboxDispatcher(
        IOutboxStore store,
        IServiceResolverFactory serviceResolverFactory,
        OutboxOptions options,
        ILogger? logger = null,
        Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _serviceResolverFactory = serviceResolverFactory;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<OutboxDispatchResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var due = await _store.ClaimDueAsync(_options.BatchSize, _options.ClaimLease, cancellationToken);

        var dispatched = 0;
        var rescheduled = 0;
        var parked = 0;
        var sentButUnsettled = 0;

        foreach (var envelope in due)
        {
            switch (await DispatchEnvelopeAsync(envelope, cancellationToken))
            {
                case OutboxDispatchOutcome.Dispatched:
                    dispatched++;
                    break;
                case OutboxDispatchOutcome.Rescheduled:
                    rescheduled++;
                    break;
                case OutboxDispatchOutcome.Parked:
                    parked++;
                    break;
                case OutboxDispatchOutcome.SentButUnsettled:
                    sentButUnsettled++;
                    break;
            }
        }

        var deleted = await _store.DeleteDispatchedBeforeAsync(_now() - _options.RetentionPeriod, cancellationToken);

        return new OutboxDispatchResult(dispatched, rescheduled, parked, deleted, sentButUnsettled);
    }

    /// <inheritdoc />
    public async Task<OutboxDispatchOutcome> DispatchOneAsync(string envelopeId, CancellationToken cancellationToken = default)
    {
        var envelope = await _store.ClaimAsync(envelopeId, _options.ClaimLease, cancellationToken);
        if (envelope == null)
        {
            return OutboxDispatchOutcome.ClaimRefused;
        }

        return await DispatchEnvelopeAsync(envelope, cancellationToken);
    }

    private async Task<OutboxDispatchOutcome> DispatchEnvelopeAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        using var scope = _serviceResolverFactory.CreateScope();

        // Every envelope handed to this method just came from ClaimDueAsync/ClaimAsync, which always
        // stamps LeaseToken - see IOutboxStore.ClaimDueAsync's remarks.
        var leaseToken = envelope.LeaseToken
            ?? throw new InvalidOperationException(
                $"Outbox envelope '{envelope.Id}' reached dispatch with no LeaseToken - it must come from a store's ClaimDueAsync/ClaimAsync.");

        try
        {
            var dispatchScope = scope.GetService<OutboxDispatchScope>();
            dispatchScope.Begin(envelope.Headers);

            var serializer = scope.GetService<ISerializer>();
            var payloadType = Type.GetType(envelope.PayloadType, throwOnError: true)!;
            var payload = serializer.Deserialize(payloadType, envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Deserializing outbox envelope '{envelope.Id}' payload as '{envelope.PayloadType}' returned null.");

            var sender = scope.GetService<IBenzeneMessageSender>();
            await sender.SendAsync<object, Void>(envelope.Topic, payload, new Dictionary<string, string>(envelope.Headers));

            // The send above genuinely happened. From here on a settle failure must NEVER be routed
            // through the catch block below - that block's reschedule/park logic exists for a send that
            // failed, and applying it to a settle-call throw after a real send would guarantee a
            // duplicate delivery on a routine transient store error (the bug this split fixes). See
            // MarkDispatchedWithRetryAsync's remarks for the dedicated handling.
            return await MarkDispatchedWithRetryAsync(envelope, leaseToken, cancellationToken);
        }
        catch (Exception ex)
        {
            var nextAttempt = envelope.AttemptCount + 1;
            var error = DescribeError(ex);

            if (nextAttempt >= _options.MaxAttempts)
            {
                _logger.LogError(ex,
                    "Outbox envelope {EnvelopeId} for topic {Topic} failed on attempt {Attempt}/{MaxAttempts}; " +
                    "parking (no further automatic retries).",
                    envelope.Id, envelope.Topic, nextAttempt, _options.MaxAttempts);
                var parked = await _store.ParkAsync(envelope.Id, error, leaseToken, cancellationToken);
                if (!parked)
                {
                    _logger.LogWarning(
                        "Outbox envelope {EnvelopeId} for topic {Topic} was reclaimed by another worker before " +
                        "this attempt's park could be recorded; outcome recorded by the new holder.",
                        envelope.Id, envelope.Topic);
                }
                return OutboxDispatchOutcome.Parked;
            }

            var delay = ComputeBackoff(nextAttempt);
            _logger.LogWarning(ex,
                "Outbox envelope {EnvelopeId} for topic {Topic} failed on attempt {Attempt}/{MaxAttempts}; " +
                "rescheduling in {Delay}.",
                envelope.Id, envelope.Topic, nextAttempt, _options.MaxAttempts, delay);
            var rescheduled = await _store.RescheduleAsync(envelope.Id, nextAttempt, delay, error, leaseToken, cancellationToken);
            if (!rescheduled)
            {
                _logger.LogWarning(
                    "Outbox envelope {EnvelopeId} for topic {Topic} was reclaimed by another worker before " +
                    "this attempt's reschedule could be recorded; outcome recorded by the new holder.",
                    envelope.Id, envelope.Topic);
            }
            return OutboxDispatchOutcome.Rescheduled;
        }
    }

    /// <summary>
    /// Settles a genuinely successful send. A <see cref="IOutboxStore.MarkDispatchedAsync"/> that
    /// returns <see langword="false"/> (reclaimed by another worker) is handled exactly as before - a
    /// warning, since the new lease holder now owns the outcome. A <see cref="IOutboxStore.MarkDispatchedAsync"/>
    /// that <em>throws</em> (a routine transient store error - throttling, a network blip) is a
    /// different failure mode entirely from a failed send, and must never be treated like one: doing so
    /// would reschedule/park an envelope that was already delivered, guaranteeing a duplicate. Instead:
    /// log at error level, retry the settle once, and if it still throws, deliberately do nothing further
    /// - the envelope stays exactly as claimed (still <see cref="OutboxStatus.Pending"/> in the store,
    /// lease outstanding) so the next sweep's <see cref="IOutboxStore.ClaimDueAsync"/> reclaims it once
    /// that lease naturally lapses, the same recovery path any other lost/stalled claim already uses.
    /// This method never throws - a settle failure must remain visible/recoverable, never swallowed
    /// silently and never allowed to escape as a raw exception either.
    /// </summary>
    private async Task<OutboxDispatchOutcome> MarkDispatchedWithRetryAsync(OutboxEnvelope envelope, string leaseToken, CancellationToken cancellationToken)
    {
        const int maxSettleAttempts = 2;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxSettleAttempts; attempt++)
        {
            try
            {
                var settled = await _store.MarkDispatchedAsync(envelope.Id, leaseToken, cancellationToken);
                if (!settled)
                {
                    // The lease was reclaimed by another worker before this send's settle wrote - that
                    // worker's own claim/dispatch/settle now owns this envelope's fate. Warn, don't
                    // error: this is the fencing contract working as designed under contention, not a
                    // failure of this attempt's send (which did happen).
                    _logger.LogWarning(
                        "Outbox envelope {EnvelopeId} for topic {Topic} was reclaimed by another worker before " +
                        "this attempt's dispatch could be recorded; outcome recorded by the new holder.",
                        envelope.Id, envelope.Topic);
                }
                return OutboxDispatchOutcome.Dispatched;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogError(ex,
                    "Outbox envelope {EnvelopeId} for topic {Topic} was sent successfully, but settling it " +
                    "(MarkDispatchedAsync) threw on attempt {Attempt}/{MaxAttempts} - this is a store failure, " +
                    "not a failed send.",
                    envelope.Id, envelope.Topic, attempt, maxSettleAttempts);
            }
        }

        // Both settle attempts threw. Do NOT drive the reschedule/park path (it would treat "sent" as
        // "failed to send", guaranteeing a duplicate resend), and do NOT swallow the failure (the
        // envelope must remain visible/recoverable) - leave it claimed for the sweeper, marked
        // sent-but-unsettled so an operator/the sweeper can tell this apart from a send that actually
        // failed.
        _logger.LogError(lastError,
            "Outbox envelope {EnvelopeId} for topic {Topic} is SENT-BUT-UNSETTLED: the send succeeded but " +
            "its dispatched state could not be recorded after a retry. Leaving it claimed for the sweeper to " +
            "reclaim once its lease lapses, rather than rescheduling/parking it as a failed send.",
            envelope.Id, envelope.Topic);
        return OutboxDispatchOutcome.SentButUnsettled;
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        // Guard against Math.Pow overflow on a very high attempt count before it ever reaches
        // BackoffCap - clamp the exponent so 2^exponent can't exceed the cap by an absurd margin.
        var cappedExponent = Math.Min(exponent, 32);
        var seconds = _options.BackoffBase.TotalSeconds * Math.Pow(2, cappedExponent);
        var cappedSeconds = Math.Min(seconds, _options.BackoffCap.TotalSeconds);
        return TimeSpan.FromSeconds(cappedSeconds);
    }

    private static string DescribeError(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
