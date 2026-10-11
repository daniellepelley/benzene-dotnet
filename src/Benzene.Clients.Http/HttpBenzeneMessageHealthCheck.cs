using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Serialization;
using Benzene.HealthChecks.Core;
using Benzene.Results;

namespace Benzene.Clients.Http;

/// <summary>
/// Verifies reachability of a downstream Benzene service over its BenzeneMessage HTTP endpoint by POSTing a
/// non-destructive <c>healthcheck</c>-topic envelope (the same deep-healthcheck topic the framework uses) and
/// treating a 2xx envelope response as healthy. This is the HTTP-envelope counterpart of the SDK client
/// reachability checks (<c>SqsHealthCheck</c>, …): it proves the target endpoint is reachable and answering,
/// without side effects.
/// </summary>
/// <remarks>
/// Failures follow the shared §3.9 policy (reversed): a permission response (HTTP 401/403) is a
/// <b>persistent</b> <see cref="HealthCheckStatus.Failed"/> (<see cref="IHealthCheckResult.IsPersistent"/>) —
/// it surfaces as unhealthy even for the auto-wired dependency check rather than being softened to a Warning,
/// because a denied probe is a deterministic misconfiguration that won't self-heal — a transport exception is
/// classified via <see cref="HealthCheckError.Classify"/> (<see cref="HealthCheckStatus.Failed"/>), and any
/// other mapped non-2xx (e.g. a target that doesn't route <c>healthcheck</c> → 404) is reported unhealthy so a
/// mis-wired target surfaces. The reported <c>Url</c>/<see cref="HealthCheckDependency"/> have any basic-auth
/// userinfo stripped so credentials can't leak into a health report; the request itself still uses the full URL.
/// The exception <b>message</b> is never reported (secret-safety) — only its type.
/// <para>
/// Auto-wired by <c>AddHttpBenzeneMessageClient(url)</c> onto the dependency category (deep <c>healthcheck</c>
/// layer only, never a Kubernetes probe — see <see cref="IDependencyHealthCheck"/>). Requires an
/// <see cref="HttpClient"/> in DI, same as the client.
/// </para>
/// </remarks>
public class HttpBenzeneMessageHealthCheck : IHealthCheck
{
    private static readonly ISerializer Serializer = JsonSerializer.Shared;

    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly string _healthCheckTopic;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>Initializes a new instance of the <see cref="HttpBenzeneMessageHealthCheck"/> class.</summary>
    /// <param name="httpClient">The client used to POST the probe. Its lifetime is the caller's responsibility.</param>
    /// <param name="url">The target BenzeneMessage endpoint URL to probe (the same URL the client posts to).</param>
    /// <param name="healthCheckTopic">The topic to POST (defaults to <c>benzene:healthcheck</c>) — pass the topic the target routes its deep health check on.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token to pass to the request; null observes no cancellation.</param>
    public HttpBenzeneMessageHealthCheck(HttpClient httpClient, string url, string healthCheckTopic = Benzene.Abstractions.BenzeneTopic.HealthCheck, ICancellationTokenAccessor? cancellation = null)
    {
        _httpClient = httpClient;
        _url = url;
        _healthCheckTopic = healthCheckTopic;
        _cancellation = cancellation;
    }

    /// <inheritdoc />
    public string Type => "HttpBenzeneMessage";

    /// <summary>POSTs the healthcheck-topic envelope and reports the outcome under the §3.9 policy.</summary>
    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
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
            var envelope = new BenzeneMessageEnvelope
            {
                Topic = _healthCheckTopic,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Body = ""
            };

            using var content = new StringContent(Serializer.Serialize(envelope), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_url, content, token);

            var statusCode = (int)response.StatusCode;
            var data = new Dictionary<string, object> { { "Url", reportedUrl }, { "StatusCode", statusCode } };

            // The envelope endpoint answers 200 for every envelope it processed (transport-bindings §1), so
            // the outcome is the envelope in the body - read it whatever the HTTP status; only a body that is
            // not an envelope falls back to the reverse HTTP table.
            var responseBody = await response.Content.ReadAsStringAsync(token);
            var reply = EnvelopeResponseReader.Read(response.StatusCode, responseBody,
                response.Content.Headers.ContentType?.MediaType, out var isEnvelope);
            if (isEnvelope)
            {
                data["BenzeneStatus"] = reply.StatusCode;
            }

            // A permission response probing the endpoint (401/403 from the host, or unauthorized/forbidden
            // from the target's own auth middleware) is a persistent, deterministic fault (§3.9) - it
            // surfaces as unhealthy even for the auto-wired dependency check, rather than being softened to
            // a Warning that hides a real access break.
            if (HealthCheckError.IsAuthorizationFailure(statusCode)
                || reply.StatusCode is BenzeneResultStatus.Unauthorized or BenzeneResultStatus.Forbidden)
            {
                return HealthCheckResult.CreatePersistentFailure(Type, data, dependencies);
            }

            // Reachable and answered: healthy when the health query succeeded (any status but a failure -
            // e.g. not-found from a target that routes no health topic is unhealthy) and, when the body is
            // the target's health report, the report says healthy (wire-contracts §5: an unhealthy target
            // answers ok with isHealthy false).
            var succeeded = reply.IsSuccessful == true;
            var healthy = succeeded && ReportedHealth(reply.Body) != false;
            return HealthCheckResult.CreateInstance(healthy, Type, data, dependencies);
        }
        catch (Exception ex)
        {
            return HealthCheckError.Classify(Type, ex, dependencies,
                data: new Dictionary<string, object> { { "Url", reportedUrl } });
        }
    }

    // The target's own isHealthy verdict, when the envelope body is its health report; null otherwise.
    private static bool? ReportedHealth(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "isHealthy", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    return property.Value.GetBoolean();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    // Strip any userinfo (basic-auth credentials) from the reported URL - the report can flow out to whoever
    // calls the healthcheck topic with no authorization, so a "https://user:pass@host" URL must not leak them.
    // The request itself still uses the full URL.
    private static string StripUserInfo(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
        }

        return url;
    }
}
