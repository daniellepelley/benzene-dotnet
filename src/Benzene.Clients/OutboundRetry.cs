using Benzene.Abstractions.Results;
using Benzene.Results;

namespace Benzene.Clients;

/// <summary>
/// Ready-made <c>shouldRetryContext</c> predicates for retrying an outbound send on its result.
/// </summary>
/// <remarks>
/// <para>
/// A send that fails in transport does not throw: the transport converter records it on
/// <see cref="OutboundContext.Response"/> as a failure result (<c>service-unavailable</c>, or
/// <c>timeout</c> when the deadline elapsed - see <see cref="TransportFailure"/>). So
/// <c>Benzene.Resilience</c>'s <c>UseRetry</c>, which by default retries only a thrown exception, never
/// sees a failed send. These predicates are what to pass as its <c>shouldRetryContext</c>:
/// </para>
/// <code>
/// routing.Route("order:create", pipeline =&gt; pipeline
///     .UseRetry(3, shouldRetryContext: OutboundRetry.Transient)
///     .UseSqs(queueUrl));
/// </code>
/// <para>
/// <c>UseRetry</c> goes <b>before</b> the transport (<c>UseSqs</c>, <c>UseSns</c>, ...): middleware runs
/// in registration order and the transport ends the pipeline, so anything registered after it never
/// runs.
/// </para>
/// </remarks>
public static class OutboundRetry
{
    /// <summary>
    /// Retries a send whose result is <c>service-unavailable</c> or <c>too-many-requests</c> - the
    /// failures a later attempt may get past, and that left nothing applied. <c>timeout</c> is left
    /// out: it leaves unknown whether the send was applied, so retrying it blindly is only safe for
    /// an idempotent send - opt in with <see cref="OnStatus"/> where it is.
    /// </summary>
    /// <param name="context">The outbound context after an attempt.</param>
    public static bool Transient(OutboundContext context)
    {
        return context.Response is IBenzeneResult result
               && (result.Status == BenzeneResultStatus.ServiceUnavailable
                   || result.Status == BenzeneResultStatus.TooManyRequests);
    }

    /// <summary>
    /// Retries any unsuccessful result, whatever its status - including ones a retry cannot fix, such
    /// as <c>validation-error</c>. Prefer <see cref="Transient"/> unless the receiver's failures are
    /// known to be transient.
    /// </summary>
    /// <param name="context">The outbound context after an attempt.</param>
    public static bool Unsuccessful(OutboundContext context)
    {
        return context.Response is IBenzeneResult { IsSuccessful: false };
    }

    /// <summary>
    /// Retries a result whose status is one of <paramref name="statuses"/> (e.g.
    /// <c>OutboundRetry.OnStatus(BenzeneResultStatus.ServiceUnavailable, BenzeneResultStatus.Timeout)</c>
    /// for an idempotent send).
    /// </summary>
    /// <param name="statuses">The statuses to retry.</param>
    public static Func<OutboundContext, bool> OnStatus(params string[] statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var set = new HashSet<string>(statuses, StringComparer.Ordinal);
        return context => context.Response is IBenzeneResult result && set.Contains(result.Status);
    }
}
