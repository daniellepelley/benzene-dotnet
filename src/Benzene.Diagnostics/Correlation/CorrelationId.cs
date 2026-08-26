using Benzene.Abstractions;

namespace Benzene.Diagnostics.Correlation;

/// <summary>
/// Default <see cref="ICorrelationId"/> holder: a per-scope value that starts out self-generated
/// and can be overridden once by a caller-supplied value (typically an inbound correlation header via
/// <see cref="InboundCorrelationIdMiddleware{TContext}"/>).
/// </summary>
public class CorrelationId : ICorrelationId
{
    /// <summary>
    /// Maximum accepted length for a caller-supplied correlation id. Longer values are rejected (the
    /// self-generated id is kept) rather than truncated, since silently truncating could make two
    /// distinct caller-supplied ids collide.
    /// </summary>
    public const int MaxLength = 128;

    private string _correlationId = Guid.NewGuid().ToString();

    /// <summary>
    /// Overrides the correlation id with a caller-supplied value, subject to a boundary check: this is
    /// the point where an inbound, untrusted header value is accepted into a process-wide sink (log
    /// scopes via <c>ILogger.BeginScope</c>, and outbound headers on this service's own downstream
    /// calls). A value is rejected - the current (self-generated, by default) id is left in place -
    /// when it is null/empty, longer than <see cref="MaxLength"/>, or contains any control
    /// character (including <c>\r</c>/<c>\n</c>, which could otherwise forge extra log lines or inject
    /// extra response/request headers via CR/LF). <see cref="ICorrelationId"/>'s "always has a value"
    /// contract holds either way - a rejected value simply never displaces the existing one.
    /// </summary>
    /// <param name="correlationId">The candidate correlation id.</param>
    public void Set(string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId) || correlationId.Length > MaxLength)
        {
            return;
        }

        foreach (var c in correlationId)
        {
            if (char.IsControl(c))
            {
                return;
            }
        }

        _correlationId = correlationId;
    }

    public string Get()
    {
        return _correlationId;
    }
}