using System.Diagnostics;
using System.Text;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;

namespace Benzene.ClaimCheck;

/// <summary>
/// Inbound middleware that resolves a claim-check reference carried on the wire header named by
/// <see cref="ClaimCheckOptions.HeaderName"/> back to the real body, replacing the message's raw body
/// with it before deserialization. See <c>work/claim-check-plan.md</c> §1 for the full design
/// reasoning.
/// </summary>
/// <remarks>
/// <para>
/// Place it on the transport's inbound pipeline, after the observability prelude (so a store fetch
/// appears in the trace) and before <c>UseMessageHandlers</c> (the deserialization boundary):
/// <code>
/// aws.UseSqs(sqs =&gt; Observe(sqs)
///     .UseClaimCheck()          // after tracing, before the handlers
///     .UseMessageHandlers(handlers, …));
/// </code>
/// </para>
/// <para>
/// A message with no claim-check header passes through untouched, without touching the store - most
/// messages never offload, so this must stay free. A message that carries the header but resolves to
/// no stored body throws <see cref="ClaimCheckNotFoundException"/> - never a silent skip - so the
/// transport's normal failure semantics (nack, redelivery, eventually a DLQ) apply.
/// </para>
/// <para>
/// Hydration needs an <see cref="IMessageBodySetter{TContext}"/> for <typeparamref name="TContext"/>
/// to replace the raw body - an abstraction most transports do not yet implement (see
/// <c>work/claim-check-plan.md</c> Phase 2). When none is registered, this middleware fails loud with
/// an <see cref="InvalidOperationException"/> naming the context type, rather than silently leaving
/// the placeholder body for the handler to choke on.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The transport-specific message context type.</typeparam>
public class ClaimCheckHydrateMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly IClaimCheckStore _store;
    private readonly IMessageHeadersGetter<TContext> _headersGetter;
    private readonly IMessageBodySetter<TContext>? _bodySetter;
    private readonly ClaimCheckOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimCheckHydrateMiddleware{TContext}"/> class.
    /// </summary>
    /// <param name="store">The store claim-checked bodies are resolved from.</param>
    /// <param name="headersGetter">Reads the inbound message's headers for
    /// <typeparamref name="TContext"/>.</param>
    /// <param name="bodySetter">Replaces the inbound message's raw body for
    /// <typeparamref name="TContext"/>, or <c>null</c> when no implementation is registered for this
    /// transport - resolved with <c>TryGetService</c> so its absence surfaces as a descriptive error
    /// only when a message actually needs hydrating, not for every transport whether it ever carries
    /// a claim check or not.</param>
    /// <param name="options">Header name configuration.</param>
    public ClaimCheckHydrateMiddleware(
        IClaimCheckStore store,
        IMessageHeadersGetter<TContext> headersGetter,
        IMessageBodySetter<TContext>? bodySetter,
        ClaimCheckOptions options)
    {
        _store = store;
        _headersGetter = headersGetter;
        _bodySetter = bodySetter;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => nameof(ClaimCheckHydrateMiddleware<TContext>);

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var headers = _headersGetter.GetHeaders(context);
        if (headers == null || !TryGetHeader(headers, _options.HeaderName, out var reference) || string.IsNullOrWhiteSpace(reference))
        {
            await next();
            return;
        }

        if (_bodySetter == null)
        {
            throw new InvalidOperationException(
                $"This message carries a claim-check reference ('{_options.HeaderName}': '{reference}'), " +
                $"but no IMessageBodySetter<{typeof(TContext).Name}> is registered, so it cannot be " +
                $"hydrated. Register an IMessageBodySetter<{typeof(TContext).Name}> for this transport, " +
                $"or remove UseClaimCheck<{typeof(TContext).Name}>() from the pipeline.");
        }

        var body = await _store.GetAsync(reference!);
        if (body == null)
        {
            throw new ClaimCheckNotFoundException(reference!);
        }

        await _bodySetter.SetBody(context, body);

        var activity = Activity.Current;
        if (activity != null)
        {
            activity.SetTag("benzene.claim-check", "hydrated");
            activity.SetTag("benzene.claim-check.bytes", Encoding.UTF8.GetByteCount(body));
        }

        await next();
    }

    // Header keys are case-insensitive on read regardless of the concrete IMessageHeadersGetter's
    // dictionary comparer (wire-contracts.md §2) - the same rule and shape as
    // HeaderOrBodyHashIdempotencyKeyStrategy's own TryGetHeader.
    private static bool TryGetHeader(IDictionary<string, string> headers, string key, out string? value)
    {
        if (headers.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
