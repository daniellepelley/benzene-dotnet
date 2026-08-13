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
            }
        }

        var deleted = await _store.DeleteDispatchedBeforeAsync(_now() - _options.RetentionPeriod, cancellationToken);

        return new OutboxDispatchResult(dispatched, rescheduled, parked, deleted);
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

            await _store.MarkDispatchedAsync(envelope.Id, cancellationToken);
            return OutboxDispatchOutcome.Dispatched;
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
                await _store.ParkAsync(envelope.Id, error, cancellationToken);
                return OutboxDispatchOutcome.Parked;
            }

            var delay = ComputeBackoff(nextAttempt);
            _logger.LogWarning(ex,
                "Outbox envelope {EnvelopeId} for topic {Topic} failed on attempt {Attempt}/{MaxAttempts}; " +
                "rescheduling in {Delay}.",
                envelope.Id, envelope.Topic, nextAttempt, _options.MaxAttempts, delay);
            await _store.RescheduleAsync(envelope.Id, nextAttempt, delay, error, cancellationToken);
            return OutboxDispatchOutcome.Rescheduled;
        }
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
