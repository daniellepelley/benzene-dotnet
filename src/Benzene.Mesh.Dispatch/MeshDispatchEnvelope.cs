using System.Collections.Generic;

namespace Benzene.Mesh.Dispatch;

/// <summary>The message to dispatch to a target service (the standard Benzene message envelope).</summary>
public class MeshDispatchEnvelope
{
    /// <summary>Initializes a new instance of the <see cref="MeshDispatchEnvelope"/> class.</summary>
    public MeshDispatchEnvelope(string topic, IDictionary<string, string> headers, string body)
    {
        Topic = topic;
        Headers = headers;
        Body = body;
    }

    /// <summary>The topic to invoke on the target service.</summary>
    public string Topic { get; }

    /// <summary>Message headers to carry.</summary>
    public IDictionary<string, string> Headers { get; }

    /// <summary>The serialized message body.</summary>
    public string Body { get; }
}

/// <summary>The response a target service returned to a dispatched message.</summary>
public class MeshDispatchResult
{
    /// <summary>Initializes a new instance of the <see cref="MeshDispatchResult"/> class.</summary>
    public MeshDispatchResult(string statusCode, string? body, IDictionary<string, string>? headers = null, bool? isSuccessful = null)
    {
        StatusCode = statusCode;
        Body = body;
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IsSuccessful = isSuccessful;
    }

    /// <summary>
    /// The Benzene status the service returned - from its response envelope, on every transport. (An HTTP
    /// target whose response was not an envelope reports the reverse-mapped HTTP status instead.)
    /// </summary>
    public string StatusCode { get; }

    /// <summary>
    /// The service's own success signal (wire-contracts.md §1.2), when it sent one - authoritative over any
    /// classification of <see cref="StatusCode"/>. Omitted from the JSON when absent.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSuccessful { get; }

    /// <summary>Any response headers the service returned.</summary>
    public IDictionary<string, string> Headers { get; }

    /// <summary>The response body the service returned.</summary>
    public string? Body { get; }

    /// <summary>
    /// The result of a dispatch that failed in transport before any reply arrived - no connection, a refused
    /// or reset request, the client's own timeout (transport-bindings.md §1, spec 1.1: a result, not an
    /// exception): <c>timeout</c> when a <see cref="TimeoutException"/> is in the exception's chain (e.g.
    /// <c>HttpClient.Timeout</c>), otherwise <c>service-unavailable</c>; unsuccessful, the exception's message
    /// as the body. The caller's own cancellation (a <c>UseTimeout()</c> deadline on the dispatch) is not one:
    /// it still propagates.
    /// </summary>
    /// <param name="exception">What the dispatch threw.</param>
    public static MeshDispatchResult TransportFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var timedOut = false;
        for (var current = exception; current != null && !timedOut; current = current.InnerException)
        {
            timedOut = current is TimeoutException;
        }

        return new MeshDispatchResult(
            timedOut ? Benzene.Results.BenzeneResultStatus.Timeout : Benzene.Results.BenzeneResultStatus.ServiceUnavailable,
            exception.Message, isSuccessful: false);
    }
}
