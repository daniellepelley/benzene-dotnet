using System.Threading;
using Benzene.Abstractions.Middleware;
using Benzene.Core;

namespace Benzene.Resilience;

/// <summary>
/// Wraps the downstream pipeline in a deadline: if <c>next()</c> has not completed within
/// <paramref name="timeout"/>, the pipeline is cancelled and the middleware translates that into a
/// service-side <see cref="TimeoutException"/> rather than letting an
/// <see cref="OperationCanceledException"/> escape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition, not replacement.</b> This middleware never invents cancellation out of nothing -
/// it reads the ambient <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/>'s current
/// token (the "original" token - <see cref="System.Threading.CancellationToken.None"/> if no
/// host has seeded one), links a new <see cref="CancellationTokenSource"/> to it via
/// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/>, and arms that
/// source with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>. The linked token becomes
/// the ambient token for the duration of <c>next()</c> only - the accessor is restored to the
/// original token in a <c>finally</c>, and the linked source (and its timer, and its registration on
/// the original token - the actual leak vector) is disposed via <c>using</c> on every path, including
/// exceptions. Nested <c>UseTimeout</c> calls compose naturally: the innermost deadline governs while
/// inside it, and each layer restores exactly what it saw on the way in, so an outer layer never
/// observes an inner layer's (by-then-disposed) linked token.
/// </para>
/// <para>
/// <b>The timeout-vs-cancellation semantic line.</b> When <c>next()</c> throws an
/// <see cref="OperationCanceledException"/>, this middleware distinguishes two cases:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>A timer somewhere in this nesting fired</b> (the <i>original</i> token this layer saw on entry
/// had not itself been cancelled) - a service-side deadline, not a genuine cancellation. Translated
/// into a <see cref="TimeoutException"/>, which does not carry a fired token, so
/// <c>ExceptionHandlerMiddleware</c> (or, inside a handler wrapper, <c>MessageHandler</c>'s own
/// <c>catch (TimeoutException)</c>) converts it into a <c>BenzeneResultStatus.Timeout</c> failure
/// result instead of triggering redelivery. Note this deliberately does NOT require the exception's
/// <see cref="OperationCanceledException.CancellationToken"/> to equal <i>this</i> layer's own
/// <c>cts.Token</c>: in nested <c>UseTimeout</c> composition, the exception's token is always the
/// innermost live <c>CancellationTokenSource</c>'s token regardless of which layer's timer actually
/// fired - an OUTER deadline firing while execution is inside an INNER wrap still throws with the
/// inner layer's token attached, because the inner layer's linked source observes its own parent (this
/// layer's <c>cts.Token</c>) being cancelled and reports itself as the source. Requiring an exact match
/// against <c>cts.Token</c> here would let that case escape this layer's catch entirely, as a raw
/// <see cref="OperationCanceledException"/>, when a timer - just not this exact CTS - is exactly what
/// fired. Comparing only against the true original/host token (never cancelled by any timer) is what
/// correctly distinguishes "some timer in this nesting fired" from "the host cancelled".
/// </description></item>
/// <item><description>
/// <b>The host's own token fired</b> (shutdown, client disconnect - <c>original.IsCancellationRequested</c>
/// is <see langword="true"/>) - a genuine cancellation. The <see cref="OperationCanceledException"/>
/// propagates untouched (the <c>when</c> filter on the <c>catch</c> below does not match), so
/// queue/settlement transports still redeliver interrupted work exactly as they do without this
/// middleware in the pipeline. This is load-bearing: weakening it would turn a clean shutdown/drain
/// into either a swallowed cancellation or a misleading "timeout" result.
/// </description></item>
/// </list>
/// <para>
/// Safe on any host, seeded or not: with no host token the "original" is simply
/// <see cref="CancellationToken.None"/> (which can never itself request cancellation), so the timer is
/// the only source that can fire and every timeout is unambiguously translated.
/// </para>
/// </remarks>
public class TimeoutMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly CancellationTokenAccessor _accessor;
    private readonly TimeSpan _timeout;

    public TimeoutMiddleware(CancellationTokenAccessor accessor, TimeSpan timeout)
    {
        _accessor = accessor;
        _timeout = timeout;
    }

    public string Name => "Timeout";

    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var original = _accessor.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(original);
        cts.CancelAfter(_timeout);
        _accessor.CancellationToken = cts.Token;
        try
        {
            await next();
        }
        catch (OperationCanceledException ex)
            when (!original.IsCancellationRequested)
        {
            // The timer fired, not the host: a service-side timeout, not a genuine cancellation.
            throw new TimeoutException($"Pipeline exceeded the configured timeout of {_timeout}.", ex);
        }
        finally
        {
            _accessor.CancellationToken = original;
        }
    }
}
