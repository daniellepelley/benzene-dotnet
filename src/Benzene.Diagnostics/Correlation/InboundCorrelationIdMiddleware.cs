using Benzene.Abstractions;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;

namespace Benzene.Diagnostics.Correlation;

/// <summary>
/// Inbound middleware that restores the caller's correlation id: it reads the correlation header off
/// the incoming message (via the transport's <see cref="IMessageHeadersGetter{TContext}"/>) and seeds
/// <see cref="ICorrelationId"/> with it, so this service's logs, traces and its own outbound sends
/// continue the caller's chain instead of starting a fresh one.
/// </summary>
/// <remarks>
/// The inbound counterpart of <c>Benzene.Clients.CorrelationId.CorrelationIdMiddleware</c>, which
/// stamps the same header on the way out. Transport-agnostic in exactly the way
/// <see cref="W3CTraceContextExtensions.UseW3CTraceContext{TContext}"/> is: it works on any pipeline
/// whose context has an <see cref="IMessageHeadersGetter{TContext}"/> registered.
/// </remarks>
/// <typeparam name="TContext">The transport context type this pipeline handles.</typeparam>
public class InboundCorrelationIdMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly ICorrelationId _correlationId;
    private readonly IMessageHeadersGetter<TContext> _headersGetter;
    private readonly string _correlationKey;

    /// <summary>Initializes a new instance of the <see cref="InboundCorrelationIdMiddleware{TContext}"/> class.</summary>
    /// <param name="correlationId">The correlation-id holder to seed.</param>
    /// <param name="headersGetter">The transport's headers getter, used to read the correlation header.</param>
    /// <param name="correlationKey">
    /// The header key to read. Defaults to <see cref="CorrelationHeaderDefaults.HeaderKey"/> - the same
    /// default the outbound stamping middleware writes and the inbound diagnostics tag reads, so the
    /// two directions join up without configuration.
    /// </param>
    public InboundCorrelationIdMiddleware(ICorrelationId correlationId, IMessageHeadersGetter<TContext> headersGetter,
        string correlationKey = CorrelationHeaderDefaults.HeaderKey)
    {
        _correlationId = correlationId;
        _headersGetter = headersGetter;
        _correlationKey = correlationKey;
    }

    /// <summary>Gets the name of this middleware.</summary>
    public string Name => nameof(InboundCorrelationIdMiddleware<TContext>);

    /// <summary>
    /// Seeds <see cref="ICorrelationId"/> from the inbound correlation header, then continues the
    /// pipeline. A missing or empty header leaves the self-generated id in place - <c>ICorrelationId</c>
    /// always has a value, so an uncorrelated message still gets one. The header value is caller-
    /// controlled and untrusted: <see cref="ICorrelationId.Set"/> (see
    /// <see cref="Benzene.Diagnostics.Correlation.CorrelationId.Set"/>) rejects anything longer than
    /// <see cref="Benzene.Diagnostics.Correlation.CorrelationId.MaxLength"/> or containing a control
    /// character (notably <c>\r</c>/<c>\n</c>, which could otherwise forge extra log lines via
    /// <c>ILogger.BeginScope</c> or inject headers on this service's own outbound calls) - a rejected
    /// value silently falls back to the self-generated id rather than being accepted verbatim.
    /// </summary>
    /// <param name="context">The inbound transport context.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    public Task HandleAsync(TContext context, Func<Task> next)
    {
        // GetHeader is the public case-insensitive lookup in this same package; it returns string.Empty
        // when the header is absent, and ICorrelationId.Set ignores empty values anyway.
        var correlationId = _headersGetter.GetHeader(context, _correlationKey);

        if (!string.IsNullOrEmpty(correlationId))
        {
            _correlationId.Set(correlationId);
        }

        return next();
    }
}
