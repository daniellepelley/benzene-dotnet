using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Messages;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Http.RequestUrl;

/// <summary>
/// Strips a configured base path from the raw request and fills the scoped <see cref="IRequestUrl"/>.
/// Place it first in the HTTP pipeline: it rewrites the host's own request, so everything after it -
/// CORS, routing, the request enrichers - sees the path without the prefix. A request outside the base
/// path answers <c>not-found</c> without running the rest of the pipeline.
/// </summary>
/// <typeparam name="TContext">The host's HTTP context type.</typeparam>
public class BasePathMiddleware<TContext> : IMiddleware<TContext> where TContext : IHttpContext
{
    private readonly RequestUrlOptions _options;
    private readonly string _basePath;
    private readonly IHttpRequestLocation<TContext> _location;
    private readonly IHttpRequestAdapter<TContext> _requestAdapter;
    private readonly RequestUrlHolder _holder;
    private readonly IMessageHandlerResultSetter<TContext>? _resultSetter;

    public BasePathMiddleware(RequestUrlOptions options, IHttpRequestLocation<TContext> location,
        IHttpRequestAdapter<TContext> requestAdapter, RequestUrlHolder holder,
        IMessageHandlerResultSetter<TContext>? resultSetter)
    {
        _options = options;
        _basePath = NormalizeBasePath(options.BasePath);
        _location = location;
        _requestAdapter = requestAdapter;
        _holder = holder;
        _resultSetter = resultSetter;
    }

    public string Name => "BasePath";

    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var path = _location.GetPath(context);
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        if (_basePath.Length > 0)
        {
            if (!TryStrip(path, _basePath, out var remainder))
            {
                await NotFoundAsync(context, path);
                return;
            }

            _location.SetBasePath(context, _basePath, remainder);
            path = remainder;
        }

        var (scheme, host) = SchemeAndHost(context);
        _holder.Set(scheme, host, _basePath, path);
        await next();
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="basePath"/> or under it, ignoring case (route
    /// matching ignores case too), and what remains: <c>/tasks/1</c> under <c>/tasks</c> leaves
    /// <c>/1</c>, and <c>/tasks</c> itself leaves <c>/</c>. <c>/tasksX</c> is not under <c>/tasks</c>.
    /// </summary>
    public static bool TryStrip(string path, string basePath, out string remainder)
    {
        remainder = path;
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == basePath.Length)
        {
            remainder = "/";
            return true;
        }

        if (path[basePath.Length] != '/')
        {
            return false;
        }

        remainder = path.Substring(basePath.Length);
        return true;
    }

    /// <summary>
    /// <c>tasks</c>, <c>/tasks</c> and <c>/tasks/</c> all mean <c>/tasks</c>; empty and <c>/</c> mean no
    /// base path.
    /// </summary>
    public static string NormalizeBasePath(string? basePath)
    {
        var trimmed = (basePath ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private (string Scheme, string Host) SchemeAndHost(TContext context)
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            && Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var publicBase))
        {
            return (publicBase.Scheme, publicBase.Authority);
        }

        var headers = _requestAdapter.Map(context).Headers ?? new Dictionary<string, string>();
        var host = _options.TrustForwardedHost ? Header(headers, "x-forwarded-host") : null;
        host ??= Header(headers, "host") ?? string.Empty;

        var scheme = Header(headers, "x-forwarded-proto") ?? _location.GetScheme(context) ?? "https";
        return (scheme, host);
    }

    // A forwarded header can carry a list ("https, http" after several proxies); the first is the
    // client-facing one.
    private static string? Header(IDictionary<string, string> headers, string name)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Split(',')[0].Trim();
            }
        }

        return null;
    }

    private async Task NotFoundAsync(TContext context, string path)
    {
        if (_resultSetter == null)
        {
            return;
        }

        var detail = $"'{path}' is outside this service's base path '{_basePath}'.";
        await _resultSetter.SetResultAsync(context, new MessageHandlerResult(
            new Topic("not-found"),
            MessageHandlerDefinition.CreateInstance("not-found", typeof(Void), typeof(Void)),
            BenzeneResult.NotFound(detail)));
    }
}
