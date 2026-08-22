using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Http;

/// <summary>Terminal middleware that sends the context's <see cref="HttpRequestMessage"/> and records the response.</summary>
public class HttpClientMiddleware : IMiddleware<HttpSendMessageContext>, ITerminalMiddleware
{
    private readonly HttpClient _httpClient;
    private readonly ICancellationTokenAccessor _cancellationTokenAccessor;

    /// <summary>Initializes a new instance of the <see cref="HttpClientMiddleware"/> class with no cancellation-token accessor.</summary>
    /// <param name="httpClient">The client to send with.</param>
    public HttpClientMiddleware(HttpClient httpClient)
        : this(httpClient, null)
    {
    }

    /// <summary>
    /// Initializes the middleware, additionally resolving the ambient cancellation token so an
    /// upstream cancel/timeout aborts the outbound request instead of running it to completion.
    /// </summary>
    public HttpClientMiddleware(HttpClient httpClient, ICancellationTokenAccessor cancellationTokenAccessor)
    {
        _httpClient = httpClient;
        _cancellationTokenAccessor = cancellationTokenAccessor;
    }

    /// <summary>Gets the name of this middleware.</summary>
    public string Name => nameof(HttpClientMiddleware);

    /// <summary>Sends the context's request and records the response. Terminal middleware; does not call <paramref name="next"/>.</summary>
    public async Task HandleAsync(HttpSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellationTokenAccessor?.CancellationToken ?? CancellationToken.None;
        context.Response = await _httpClient.SendAsync(context.Request, cancellationToken);
    }
}
