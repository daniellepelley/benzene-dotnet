using Benzene.HealthChecks.Core;

namespace Benzene.HealthChecks;

/// <summary>
/// Decorates an <see cref="IHealthCheck"/> with a caller-supplied timeout: if the wrapped check has not
/// completed within that time, <see cref="ExecuteAsync"/> returns a failed result (with an
/// <c>"Error"</c>/<c>"Timed Out"</c> data entry) instead of continuing to wait. Used internally by
/// <see cref="HealthCheckProcessor"/> to wrap every check, with the processor-wide timeout (default 10s,
/// configurable via <c>new HealthCheckProcessor(TimeSpan)</c>) or a per-check <c>IHealthCheck.Timeout</c>
/// override.
/// </summary>
/// <remarks>
/// The timeout is enforced via a linked, timeout-derived <see cref="CancellationTokenSource"/> whose
/// token is passed into the inner check's <see cref="IHealthCheck.ExecuteAsync"/> - so, provided the
/// inner check forwards that token into its own I/O (as every conforming <see cref="IHealthCheck"/>
/// implementer must), a timeout actually <b>cancels</b> the underlying call rather than merely
/// abandoning the awaited task while it keeps running to completion in the background.
/// </remarks>
internal class TimeOutHealthCheck : IHealthCheck
{
    private readonly IHealthCheck _inner;
    private readonly TimeSpan _timeout;

    /// <inheritdoc />
    public string Type => _inner.Type;

    /// <summary>Initializes a new instance of the <see cref="TimeOutHealthCheck"/> class.</summary>
    /// <param name="inner">The health check to run under a timeout.</param>
    /// <param name="timeout">The maximum time to wait for the check before reporting a timeout.</param>
    public TimeOutHealthCheck(IHealthCheck inner, TimeSpan timeout)
    {
        _inner = inner;
        _timeout = timeout;
    }

    /// <summary>
    /// Runs the wrapped check under a timeout-linked token. If it has not completed by the configured
    /// timeout, returns a failed result instead of the check's actual outcome.
    /// </summary>
    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        // Link the caller's token with the configured timeout, and pass the linked token into the
        // inner check - so the timeout genuinely cancels the in-flight I/O (not just the wait) when
        // the inner check forwards it, as every conforming implementer does.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            // await (not .Result): unwraps to the real exception rather than an AggregateException.
            return await _inner.ExecuteAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token fired from CancelAfter (the timeout), not from the caller's own token -
            // report it as a timeout. A caller-initiated cancellation (ambient shutdown) is not a
            // timeout and propagates uncaught instead.
            return HealthCheckResult.CreateInstance(false, _inner.Type, new Dictionary<string, object>
            {
                { "Error", "Timed Out" }
            });
        }
        catch (Exception ex)
        {
            // The check may still fault if TimeOutHealthCheck is used without the exception-handling
            // decorator, so guard defensively rather than relying on composition order.
            return HealthCheckResult.CreateInstance(false, _inner.Type, new Dictionary<string, object>
            {
                { "Exception", ex.GetType().Name }
            });
        }
    }
}
