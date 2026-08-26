namespace Benzene.Abstractions;

/// <summary>
/// Provides access to the correlation ID for the current request.
/// Correlation IDs enable distributed tracing across service boundaries and log aggregation.
/// </summary>
public interface ICorrelationId
{
    /// <summary>
    /// Sets the correlation ID for the current request. <paramref name="correlationId"/> may come from
    /// an untrusted, caller-controlled source (an inbound header) - implementations MUST bound its
    /// length and reject control characters (notably <c>\r</c>/<c>\n</c>) before accepting it, silently
    /// keeping the existing value on rejection, since the value can flow into log scopes and outbound
    /// headers where an unsanitized value would be a log-forging/header-injection vector. See
    /// <c>Benzene.Diagnostics.Correlation.CorrelationId</c> for the reference implementation of this
    /// contract.
    /// </summary>
    /// <param name="correlationId">The correlation ID to set.</param>
    void Set(string correlationId);

    /// <summary>
    /// Gets the correlation ID for the current request.
    /// </summary>
    /// <returns>The current correlation ID.</returns>
    string Get();
}