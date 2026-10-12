using System.Text;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Clients.Common;
using Benzene.Results;

namespace Benzene.Clients.Http;

/// <summary>Converts an <see cref="IBenzeneClientContext{TRequest,TResponse}"/> to a plain HTTP request/response.</summary>
public class HttpContextConverter<TRequest, TResponse> : IContextConverter<IBenzeneClientContext<TRequest, TResponse>, HttpSendMessageContext>, ITransportFailureMapper<IBenzeneClientContext<TRequest, TResponse>>
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

    /// <summary>
    /// Reads and disposes the response and sets the result on the client context: on success, the body
    /// deserialized as <typeparamref name="TResponse"/>; on failure, the status from the reverse HTTP table
    /// (wire-contracts §4.1) - unless the response is an <c>application/problem+json</c> document carrying
    /// <c>benzeneStatus</c>, whose status then wins (spec 1.1) - and, for a problem document, the errors
    /// rebuilt from its <c>errors</c> with <c>field</c> and <c>code</c> intact, the document itself
    /// attached for <c>result.GetProblem()</c> (core-concepts §12, error fidelity).
    /// </summary>
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

        if (!httpResponse.IsSuccessStatusCode)
        {
            // An error response is never deserialized as TResponse: it commonly carries a different
            // shape (an HTML error page, an empty body), and that would throw and mask the real HTTP
            // status. Only an application/problem+json body is read, as the problem document it declares
            // itself to be - an arbitrary endpoint's error body is not one. Its benzeneStatus, when present,
            // is the status (wire-contracts §4.1, spec 1.1: an application-defined status survives the hop);
            // the reverse table applies to every other failure.
            var status = httpResponse.StatusCode.Convert().Status;
            contextIn.Response = ProblemDocuments.IsJsonProblem(httpResponse.Content.Headers.ContentType?.MediaType)
                ? ProblemDocuments.ToFailedResultFromDocumentStatus<TResponse>(status, body, _serializer)
                : httpResponse.StatusCode.Convert<TResponse>(default!);
            return;
        }

        TResponse response = default!;
        if (!string.IsNullOrEmpty(body))
        {
            response = _serializer.Deserialize<TResponse>(body)!;
        }

        contextIn.Response = httpResponse.StatusCode.Convert(response);
    }

    /// <summary>
    /// A send that fails in transport becomes a <c>timeout</c>/<c>service-unavailable</c> result (<see cref="TransportFailure"/>) rather than an exception (transport-bindings.md §1, spec 1.1).
    /// </summary>
    /// <param name="contextIn">The context to set the result on.</param>
    /// <param name="exception">What the send threw.</param>
    public void MapTransportFailure(IBenzeneClientContext<TRequest, TResponse> contextIn, Exception exception)
    {
        contextIn.Response = TransportFailure.ToResult<TResponse>(exception);
    }
}