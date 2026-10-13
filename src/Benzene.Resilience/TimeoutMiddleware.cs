using System.Threading;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Core;
using Benzene.Results;

namespace Benzene.Resilience;

/// <summary>
/// Wraps the downstream pipeline in a deadline: if <c>next()</c> has not completed within
/// <paramref name="timeout"/>, the pipeline is cancelled and the middleware records a
/// <c>timeout</c> failure result (<c>BenzeneResultStatus.Timeout</c>) rather than letting an
/// <see cref="OperationCanceledException"/> escape.
/// </summary>
/// <remarks>
/// <para>
/// <b>A result, not an exception.</b> When the transport registers an
/// <see cref="IMessageHandlerResultSetter{TContext}"/> (every message-handling transport does; the
/// router writes <c>not-found</c> through the same seam), the deadline is recorded as a
/// <c>timeout</c> result on the context and the middleware returns normally: an HTTP caller gets a
/// 504 problem document, a queue binding settles it as a failure (redelivered, transport-bindings §1
/// obligation 8). Without a result setter (a pipeline over a context with no message-handling
/// transport) it throws a <see cref="TimeoutException"/> instead. Either way the host's own
/// cancellation still propagates untouched (below). Because the deadline is a result,
/// <c>.UseRetry(...)</c> placed outside <c>.UseTimeout(...)</c> retries a timed-out attempt only when
/// its <c>shouldRetryContext</c> says so.
/// </para>
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
    private readonly IMessageHandlerResultSetter<TContext>? _resultSetter;
    private readonly IMessageTopicGetter<TContext>? _topicGetter;

    public TimeoutMiddleware(CancellationTokenAccessor accessor, TimeSpan timeout)
        : this(accessor, timeout, null, null)
    {
    }

    /// <param name="accessor">The ambient cancellation-token accessor this middleware wraps.</param>
    /// <param name="timeout">The deadline for the downstream pipeline.</param>
    /// <param name="resultSetter">
    /// Where a deadline is recorded as a <c>timeout</c> result. <see langword="null"/> makes the
    /// middleware throw a <see cref="TimeoutException"/> instead.
    /// </param>
    /// <param name="topicGetter">Names the timed-out message's topic on the result, when available.</param>
    public TimeoutMiddleware(CancellationTokenAccessor accessor, TimeSpan timeout,
        IMessageHandlerResultSetter<TContext>? resultSetter, IMessageTopicGetter<TContext>? topicGetter)
    {
        _accessor = accessor;
        _timeout = timeout;
        _resultSetter = resultSetter;
        _topicGetter = topicGetter;
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
            var detail = $"Pipeline exceeded the configured timeout of {_timeout}.";
            if (_resultSetter is null)
            {
                throw new TimeoutException(detail, ex);
            }

            await _resultSetter.SetResultAsync(context, new TimeoutResult(TryGetTopic(context), BenzeneResult.Timeout(detail)));
        }
        finally
        {
            _accessor.CancellationToken = original;
        }
    }

    private ITopic? TryGetTopic(TContext context)
    {
        try
        {
            return _topicGetter?.GetTopic(context);
        }
        catch
        {
            // The topic only labels the result; a context that cannot name one still gets its timeout.
            return null;
        }
    }

    private sealed class TimeoutResult(ITopic? topic, IBenzeneResult result) : IMessageHandlerResult
    {
        public ITopic? Topic { get; } = topic;

        // Non-null, like the router's own failure results (MessageHandlerDefinition.Empty()): a null
        // definition tells the response mappers there is nothing to render, which would drop the
        // problem document from an HTTP timeout response.
        public IMessageHandlerDefinition? MessageHandlerDefinition { get; } = new TimeoutDefinition(topic);

        public IBenzeneResult BenzeneResult { get; } = result;
    }

    private sealed class TimeoutDefinition(ITopic? topic) : IMessageHandlerDefinition
    {
        public ITopic Topic { get; } = topic ?? new MissingTopic();
        public Type RequestType => typeof(Benzene.Abstractions.Results.Void);
        public Type ResponseType => typeof(Benzene.Abstractions.Results.Void);
        public Type HandlerType => typeof(Benzene.Abstractions.Results.Void);
    }

    private sealed class MissingTopic : ITopic
    {
        public string Id => Constants.Missing;
        public string Version => string.Empty;
    }
}
