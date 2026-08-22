namespace Benzene.Clients.Http;

/// <summary>Middleware pipeline context for sending a single outbound HTTP request.</summary>
public class HttpSendMessageContext
{
    /// <summary>Initializes a new instance of the <see cref="HttpSendMessageContext"/> class.</summary>
    /// <param name="request">The request to send.</param>
    public HttpSendMessageContext(HttpRequestMessage request)
    {
        Request = request;
    }

    /// <summary>Gets the request to send.</summary>
    public HttpRequestMessage Request { get; }

    /// <summary>Gets or sets the response, set by <see cref="HttpClientMiddleware"/> once the request completes.</summary>
    public HttpResponseMessage Response { get; set; }
}