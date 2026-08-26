using System;
using System.Net;
using System.Threading;
using Benzene.Abstractions.DI;
using Benzene.HealthChecks.Core;

namespace Benzene.HealthChecks.Http
{
    /// <summary>
    /// Checks a downstream HTTP dependency by issuing a GET request and treating a
    /// <see cref="HttpStatusCode.OK"/> response as healthy - any other status code (including
    /// non-2xx success codes) is reported unhealthy. Uses whatever timeout/retry policy is
    /// configured on the injected <see cref="HttpClient"/> (e.g. via <c>IHttpClientFactory</c>
    /// named/typed client configuration) - this type does not implement its own timeout or retry
    /// logic.
    /// </summary>
    public class HttpPingHealthCheck : IHealthCheck
    {
        private readonly HttpClient _httpClient;
        private readonly string _url;
        private readonly ICancellationTokenAccessor? _cancellation;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpPingHealthCheck"/> class.
        /// </summary>
        /// <param name="httpClient">The client used to issue the ping request.</param>
        /// <param name="url">The URL to GET.</param>
        /// <param name="cancellation">Supplies the ambient cancellation token to pass to the request; null observes no cancellation.</param>
        public HttpPingHealthCheck(HttpClient httpClient, string url, ICancellationTokenAccessor? cancellation = null)
        {
            _url = url;
            _httpClient = httpClient;
            _cancellation = cancellation;
        }

        /// <summary>
        /// Issues a GET request to the configured URL and reports healthy only for a 200 OK
        /// response. The result's <see cref="IHealthCheckResult.Data"/> always includes the
        /// checked <c>Url</c> and, when the endpoint responded at all, the response's <c>StatusCode</c>.
        /// If the endpoint does not respond at all (connection refused, DNS failure, etc.) the result
        /// still carries <c>Url</c> and the dependency entry - not just an opaque exception - so an
        /// operator with several <see cref="HttpPingHealthCheck"/>s registered can tell which endpoint
        /// is unreachable.
        /// </summary>
        public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            // Report the URL with any userinfo (basic-auth credentials) stripped - the reported Url and
            // Dependency can flow out to whoever calls the health check topic with no authorization,
            // and a "https://user:pass@host" URL would otherwise leak the credentials. The request
            // itself still uses the full URL.
            var reportedUrl = StripUserInfo(_url);
            var dependencies = new[] { new HealthCheckDependency("Http", reportedUrl) };

            // Link the token ExecuteAsync was called with (the processor's per-check timeout) with the
            // ambient accessor's token (e.g. application shutdown), if one was supplied - either source
            // cancels the request.
            using var cts = _cancellation != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.CancellationToken)
                : null;
            var token = cts?.Token ?? cancellationToken;

            try
            {
                using var response = await _httpClient.GetAsync(_url, token);
                return HealthCheckResult.CreateInstance(response.StatusCode == HttpStatusCode.OK, Type,
                    new Dictionary<string, object> { { "Url", reportedUrl }, { "StatusCode", response.StatusCode } }, dependencies);
            }
            catch (OperationCanceledException)
            {
                // Cancellation (ambient token / the processor's own per-check timeout) is not a ping
                // failure - propagate uncaught so ExceptionHandlingHealthCheck (which every check runs
                // under via HealthCheckProcessor) classifies it as the distinct "Cancelled" outcome
                // instead of an ordinary connectivity failure.
                throw;
            }
            catch (HttpRequestException ex)
            {
                // The endpoint didn't respond at all (connection refused, DNS failure, TLS failure, ...).
                // Report it as a classified failure carrying Url + the dependency entry, the same shape as
                // a non-OK status response - not the generic, identity-less result the outer
                // ExceptionHandlingHealthCheck decorator would otherwise build (Data=[Exception=...],
                // Dependencies=[]). Mirrors how the EF/SNS/SQS health checks report their own connection
                // failures. Report the exception's type name only, never its message (it can carry
                // connection details).
                return HealthCheckResult.CreateInstance(false, Type,
                    new Dictionary<string, object> { { "Url", reportedUrl }, { "Exception", ex.GetType().Name } }, dependencies);
            }
        }

        private static string StripUserInfo(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                return uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
            }

            return url;
        }

        /// <inheritdoc />
        public string Type => "HttpPing";
    }
}
