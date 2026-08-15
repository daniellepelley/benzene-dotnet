using System;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc.Test.Fakes;

public sealed class FakeHttpRequestAdapter : IHttpRequestAdapter<FakeHttpContext>
{
    public HttpRequest Map(FakeHttpContext context) => new()
    {
        Method = context.Method,
        Path = context.Path,
        Headers = context.Headers,
    };
}

/// <summary>Appends every header write to <see cref="FakeHttpContext.ResponseHeaders"/> (never
/// overwrites) so tests can assert on multiple <c>Set-Cookie</c> writes distinctly - unlike the real AWS
/// API Gateway v1 adapter this package is written to be compatible with (see this package's
/// <c>CLAUDE.md</c>'s "One Set-Cookie per response" section), which overwrites. Keeping this fake
/// permissive is deliberate: it lets a test assert exactly how many times a header was written, which a
/// silently-overwriting fake could never reveal.</summary>
public sealed class FakeResponseAdapter : IBenzeneResponseAdapter<FakeHttpContext>
{
    public void SetResponseHeader(FakeHttpContext context, string headerKey, string headerValue)
    {
        context.ResponseHeaders.Add((headerKey, headerValue));
        if (string.Equals(headerKey, "content-type", StringComparison.OrdinalIgnoreCase))
        {
            context.ContentType = headerValue;
        }
    }

    public void SetContentType(FakeHttpContext context, string contentType)
    {
        context.ContentType = contentType;
        SetResponseHeader(context, "Content-Type", contentType);
    }

    public void SetStatusCode(FakeHttpContext context, string statusCode)
    {
        context.StatusCode = int.Parse(statusCode);
    }

    public void SetBody(FakeHttpContext context, string body)
    {
        context.Body = body;
    }

    public string GetBody(FakeHttpContext context) => context.Body ?? string.Empty;

    public Task FinalizeAsync(FakeHttpContext context)
    {
        context.Finalized = true;
        return Task.CompletedTask;
    }
}

public sealed class FakeQueryStringReader : IOidcQueryStringReader<FakeHttpContext>
{
    public System.Collections.Generic.IDictionary<string, string> GetQueryParameters(FakeHttpContext context)
        => context.QueryParameters;
}
