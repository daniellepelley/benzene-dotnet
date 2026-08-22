namespace Benzene.Clients
{
    /// <summary>
    /// The response envelope returned by a Benzene service invoked through a message client. Deserializes
    /// from the standard Benzene message envelope (<c>{ "statusCode": ..., "isSuccessful": ..., "headers": { ... }, "body": "..." }</c>)
    /// written by the serving side's <c>BenzeneMessageResponse</c> — see
    /// <c>docs/specification/wire-contracts.md</c>. <see cref="StatusCode"/> carries a Benzene result status
    /// (e.g. <c>"ok"</c>, <c>"not-found"</c>); numeric HTTP status codes from older or HTTP-shaped services
    /// are also tolerated by <c>AsBenzeneResult</c>.
    /// </summary>
    public class BenzeneMessageClientResponse
    {
        /// <summary>Initializes a new instance of the <see cref="BenzeneMessageClientResponse"/> class.</summary>
        /// <param name="statusCode">The Benzene result status (or tolerated numeric HTTP status code).</param>
        /// <param name="body">The response body.</param>
        /// <param name="headers">The response headers; defaults to an empty, case-insensitive dictionary.</param>
        /// <param name="isSuccessful">The sender's authoritative success/failure signal, when present.</param>
        public BenzeneMessageClientResponse(string statusCode, string body, IDictionary<string, string>? headers = null, bool? isSuccessful = null)
        {
            StatusCode = statusCode;
            Body = body;
            Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IsSuccessful = isSuccessful;
        }

        /// <summary>Gets the Benzene result status (e.g. <c>"ok"</c>, <c>"not-found"</c>), or a tolerated numeric HTTP status code.</summary>
        public string StatusCode { get; }

        /// <summary>
        /// The sender's authoritative success/failure signal, when present. <c>null</c> means the sender
        /// didn't write it — an older .NET service, or a language port that hasn't picked up
        /// wire-contracts.md's <c>isSuccessful</c> field yet — in which case <c>AsBenzeneResult</c> falls
        /// back to classifying <see cref="StatusCode"/> against the known status vocabulary.
        /// </summary>
        public bool? IsSuccessful { get; }

        /// <summary>Gets the response headers.</summary>
        public IDictionary<string, string> Headers { get; }

        /// <summary>Gets the response body.</summary>
        public string Body { get; }
    }
}
