using System.Text;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Results;

namespace Benzene.Clients.Http;

/// <summary>Converts an <see cref="IBenzeneClientContext{TRequest,TResponse}"/> to a plain HTTP request/response.</summary>
public class HttpContextConverter<TRequest, TResponse> : IContextConverter<IBenzeneClientContext<TRequest, TResponse>, HttpSendMessageContext>
{
    private readonly ISerializer _serializer;
    private readonly string _verb;
    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="HttpContextConverter{TRequest,TResponse}"/> class using a JSON serializer.</summary>
    /// <param name="verb">The HTTP verb to send.</param>
    /// <param name="path">The request path (or full URL; relative composes with the <c>HttpClient</c>'s <c>BaseAddress</c>).</param>
    public HttpContextConverter(string verb, string path)
        : this(verb, path, new JsonSerializer())
    { }

    /// <summary>Initializes a new instance of the <see cref="HttpContextConverter{TRequest,TResponse}"/> class.</summary>
    /// <param name="verb">The HTTP verb to send.</param>
    /// <param name="path">The request path (or full URL).</param>
    /// <param name="serializer">The serializer used for the request body and response.</param>
    public HttpContextConverter(string verb, string path, ISerializer serializer)
    {
        _verb = verb;
        _path = path;
        _serializer = serializer;
    }

    /// <summary>Builds the outbound <see cref="HttpRequestMessage"/> from the client context.</summary>
    /// <param name="contextIn">The client context to convert.</param>
    /// <returns>A task that resolves to the built <see cref="HttpSendMessageContext"/>.</returns>
    public Task<HttpSendMessageContext> CreateRequestAsync(IBenzeneClientContext<TRequest, TResponse> contextIn)
    {
        var request = new HttpRequestMessage
        {
            Content = new StringContent(_serializer.Serialize(contextIn.Request.Message), Encoding.UTF8, "application/json"),
            // RelativeOrAbsolute so a relative path composes with the HttpClient's BaseAddress instead
            // of throwing; an absolute path still works unchanged.
            RequestUri = new Uri(_path, UriKind.RelativeOrAbsolute),
            Method = new HttpMethod(_verb)
        };

        foreach (var header in contextIn.Request.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return Task.FromResult(new HttpSendMessageContext(request));
    }

    /// <summary>Reads and disposes the response, deserializing the body as <typeparamref name="TResponse"/> on success, and sets it on the client context.</summary>
    /// <param name="contextIn">The client context to set the response on.</param>
    /// <param name="contextOut">The completed <see cref="HttpSendMessageContext"/>.</param>
    public async Task MapResponseAsync(IBenzeneClientContext<TRequest, TResponse> contextIn, HttpSendMessageContext contextOut)
    {
        // Both HttpRequestMessage and HttpResponseMessage are IDisposable and created per outbound
        // call; dispose them once the body has been read (this is the terminal step that consumes the
        // response). Not socket exhaustion today - SendAsync buffers the body - but a real
        // disposable-not-disposed gap that would bite under a future streaming completion option.
        contextOut.Request?.Dispose();
        using var httpResponse = contextOut.Response;
        var body = await httpResponse.Content.ReadAsStringAsync();

        // Only parse the body as TResponse on a success status with a non-empty body. An error
        // response commonly carries a different shape (problem+json, an HTML error page, or an empty
        // body); deserializing that as TResponse would throw and mask the real HTTP status with a
        // serialization exception. On an error we surface the mapped status with a default payload.
        TResponse response = default;
        if (httpResponse.IsSuccessStatusCode && !string.IsNullOrEmpty(body))
        {
            response = _serializer.Deserialize<TResponse>(body)!;
        }

        contextIn.Response = httpResponse.StatusCode.Convert(response);
    }
}