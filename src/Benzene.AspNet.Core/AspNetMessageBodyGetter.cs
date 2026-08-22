using Benzene.Abstractions.Messages.Mappers;
using Benzene.Http.RequestBody;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Benzene.AspNet.Core;

/// <summary>
/// Extracts the message body from an ASP.NET Core request. Serves the body from the scoped
/// <see cref="HttpRequestBodyBuffer"/> when it has been read up front (asynchronously, by
/// <see cref="BufferRequestBodyMiddleware{AspNetContext}"/> - auto-wired by <c>UseHttp(...)</c>);
/// implements <see cref="IHttpRequestBodyReader{AspNetContext}"/> to do that async read. Only if
/// nothing buffered the body (the middleware was not wired in) does it fall back to reading the
/// stream itself.
/// </summary>
public class AspNetMessageBodyGetter : IMessageBodyGetter<AspNetContext>, IHttpRequestBodyReader<AspNetContext>
{
    private readonly HttpRequestBodyBuffer _buffer;
    private readonly ILogger<AspNetMessageBodyGetter> _logger;

    /// <summary>Initializes a new instance of the <see cref="AspNetMessageBodyGetter"/> class.</summary>
    /// <param name="buffer">The scoped per-request body buffer.</param>
    /// <param name="logger">Logs the exception swallowed by the <see cref="ReadBodyAsync"/> fallback path.</param>
    public AspNetMessageBodyGetter(HttpRequestBodyBuffer buffer, ILogger<AspNetMessageBodyGetter> logger)
    {
        _buffer = buffer;
        _logger = logger;
    }

    /// <summary>
    /// Gets the request body. Returns the value buffered by the async pre-read when available;
    /// otherwise reads the stream synchronously as a fallback.
    /// </summary>
    /// <param name="context">The HTTP context to extract the body from.</param>
    /// <returns>The request body as a string, or <c>null</c> if there is no body or reading it throws.</returns>
    public string? GetBody(AspNetContext context)
    {
        return _buffer.IsBuffered
            ? _buffer.Body
            // Fallback only when nothing pre-read the body (the buffering middleware wasn't wired):
            // read synchronously, preserving the original behavior for that case.
            : ReadBodyAsync(context).GetAwaiter().GetResult();
    }

    /// <summary>Reads the request body asynchronously, without blocking a thread.</summary>
    /// <param name="context">The HTTP context to read the body from.</param>
    /// <returns>The request body as a string, or <c>null</c> if there is no body or reading it throws.</returns>
    public async Task<string?> ReadBodyAsync(AspNetContext context)
    {
        var request = context.HttpContext.Request;

        try
        {
            if (request.Body == null)
            {
                return null;
            }

            // Buffer the request so anything downstream that also reads Body still can - the original
            // getter consumed the stream once and never rewound it.
            request.EnableBuffering();

            using var sr = new StreamReader(request.Body, leaveOpen: true);
            var body = await sr.ReadToEndAsync();

            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }

            return body;
        }
        catch (Exception ex)
        {
            // Deliberate fallback: a body that can't be read is treated as no body, not a request
            // failure. Logged (rather than silently swallowed) so the failure is diagnosable instead
            // of just showing up downstream as an unexpectedly empty request.
            _logger.LogDebug(ex, "Failed to read the ASP.NET Core request body; treating it as empty.");
            return null;
        }
    }
}
