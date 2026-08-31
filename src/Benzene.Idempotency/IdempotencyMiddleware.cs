using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Idempotency;

/// <summary>
/// Middleware that de-duplicates redelivered messages on an at-least-once transport. It derives an
/// idempotency key for each message, atomically claims it in an <see cref="IIdempotencyStore"/>, and
/// only invokes the rest of the pipeline (including the handler) the first time that key is seen.
/// Duplicates short-circuit without re-running the handler.
/// </summary>
/// <remarks>
/// <para>Place it early in the pipeline — before the handler, but typically after logging/tracing so
/// duplicates are still observable.</para>
/// <para>Outcome handling: if the handler throws, or reports failure via
/// <see cref="IHasMessageResult"/>, the claim is released so the transport's redelivery reprocesses
/// the message rather than the failure being permanently suppressed. Only a successful first attempt
/// is recorded as completed.</para>
/// </remarks>
/// <typeparam name="TContext">The transport-specific message context type.</typeparam>
public class IdempotencyMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly IIdempotencyStore _store;
    private readonly IIdempotencyKeyStrategy<TContext> _keyStrategy;
    private readonly IdempotencyOptions _options;
    private readonly ILogger _logger;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyMiddleware{TContext}"/> class.
    /// </summary>
    /// <param name="store">The store claims are made against and settled through.</param>
    /// <param name="keyStrategy">Derives the idempotency key for each message.</param>
    /// <param name="options">De-duplication behaviour options.</param>
    /// <param name="logger">The logger used to record a reclaimed-claim warning. Defaults to <see cref="NullLogger.Instance"/>.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token to pass into the store calls; null observes no cancellation.</param>
    public IdempotencyMiddleware(
        IIdempotencyStore store,
        IIdempotencyKeyStrategy<TContext> keyStrategy,
        IdempotencyOptions options,
        ILogger? logger = null,
        ICancellationTokenAccessor? cancellation = null)
    {
        _store = store;
        _keyStrategy = keyStrategy;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _cancellation = cancellation;
    }

    /// <inheritdoc />
    public string Name => nameof(IdempotencyMiddleware<TContext>);

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var key = _keyStrategy.GetKey(context);
        if (key == null)
        {
            // No key derived -> this message opts out of de-duplication; process normally.
            await next();
            return;
        }

        // Read at the point of use (not captured earlier): the ambient token can be replaced by a
        // wrapping middleware (e.g. a timeout) for the duration of an inner call.
        var claim = await _store.TryClaimAsync(key, Token);
        if (!claim.Claimed)
        {
            HandleDuplicate(context, claim.ExistingRecord!);
            return;
        }

        var claimToken = claim.ClaimToken!;

        try
        {
            await next();
        }
        catch
        {
            // The handler threw. Release the claim so a redelivery can reprocess the message.
            await ReleaseAsync(key, claimToken);
            throw;
        }

        if (WasSuccessful(context))
        {
            var settled = await _store.CompleteAsync(key, claimToken, true, Token);
            if (!settled)
            {
                // The claim was reclaimed by another worker before this attempt finished (it lapsed
                // and someone else won it) - the new holder owns the outcome now, so this is expected
                // under contention, not an error.
                _logger.LogWarning(
                    "Idempotency claim for key {Key} was reclaimed by another worker before this attempt " +
                    "could complete it; outcome recorded by the new holder.", key);
            }
        }
        else
        {
            // The handler ran but reported failure. Release so the redelivery retries.
            await ReleaseAsync(key, claimToken);
        }
    }

    private async Task ReleaseAsync(string key, string claimToken)
    {
        // Every caller of this helper is itself inside a `catch { await ReleaseAsync(...); throw; }`
        // (see HandleAsync's throw/failed-result paths) - the `throw;` after this call is what
        // rethrows the ORIGINAL handler exception (or simply resumes after a failed-result release,
        // where there is no exception to protect but the principle still applies uniformly). If
        // `_store.ReleaseAsync` itself throws (a real store failure, not a fenced `false`), that new
        // exception would otherwise propagate from here and the caller's `throw;` would never run -
        // silently replacing the actual reason the message failed with an unrelated store exception.
        // Catch and log it here instead, so this method never throws and the caller's own `throw;`
        // always executes. This mirrors the established settle-never-masks rule elsewhere in the
        // codebase (see e.g. BenzeneServiceBusWorker.HandleMessageAsync's AbandonMessageAsync try/catch).
        try
        {
            var released = await _store.ReleaseAsync(key, claimToken, Token);
            if (!released)
            {
                _logger.LogWarning(
                    "Idempotency claim for key {Key} was reclaimed by another worker before this attempt " +
                    "could release it; outcome recorded by the new holder.", key);
            }
        }
        catch (Exception releaseEx)
        {
            _logger.LogError(releaseEx,
                "Releasing idempotency claim for key {Key} failed after a processing failure; the claim " +
                "may remain held until it naturally expires, at which point a redelivery can reclaim it.",
                key);
        }
    }

    // Read at the point of use (not captured earlier): the ambient token can be replaced by a
    // wrapping middleware (e.g. a timeout) for the duration of an inner call.
    private CancellationToken Token => _cancellation?.CancellationToken ?? CancellationToken.None;

    private void HandleDuplicate(TContext context, IdempotencyRecord existing)
    {
        if (existing.Status == IdempotencyStatus.InProgress
            && _options.InProgressBehavior == InProgressBehavior.Throw)
        {
            throw new IdempotencyConflictException(existing.Key);
        }

        // A completed duplicate (or an in-progress one under Skip): short-circuit without re-running
        // the handler. For transports that report completion via a message result, mark it successful
        // so the duplicate is acknowledged and removed from the queue rather than redelivered again.
        if (context is IHasMessageResult hasResult)
        {
            hasResult.MessageResult = BenzeneResult.Ok();
        }
    }

    private static bool WasSuccessful(TContext context)
    {
        // Prefer the pipeline's own result signal when the transport sets one. A result-bearing
        // transport (IHasMessageResult) that completed without ever setting MessageResult has not
        // proven success - matching the "null == failure, redeliver" convention SQS/DynamoDb always
        // had and #229 extended to SNS/S3/EventBridge - so that case must NOT fall through to true.
        if (context is IHasMessageResult hasResult)
        {
            return hasResult.MessageResult?.IsSuccessful ?? false;
        }

        // A transport with no result concept at all has no signal to be consistent with: no-throw
        // still means success here, unchanged.
        return true;
    }
}
