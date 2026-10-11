using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Clients.Http;
using Benzene.HealthChecks.Core;
using Xunit;

namespace Benzene.Test.Clients.Http;

/// <summary>
/// Coverage for <see cref="HttpBenzeneMessageHealthCheck"/> - the non-destructive reachability check that POSTs
/// a <c>healthcheck</c>-topic envelope to a target Benzene service's BenzeneMessage endpoint.
/// </summary>
public class HttpBenzeneMessageHealthCheckTest
{
    private const string Url = "https://service-b.internal/benzene/invoke";

    [Fact]
    public async Task ExecuteAsync_ReturnsOk_AndPostsAHealthcheckEnvelope_WhenTheTargetAnswers2xx()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(handler), Url);

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("HttpBenzeneMessage", result.Type);
        Assert.Equal(Url, result.Data["Url"]);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Http", dependency.Kind);
        Assert.Equal(Url, dependency.Name);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("benzene:healthcheck", doc.RootElement.GetProperty("topic").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_UsesTheConfiguredHealthCheckTopic()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(handler), Url, "benzene:ping");

        await check.ExecuteAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("benzene:ping", doc.RootElement.GetProperty("topic").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ExecuteAsync_ReturnsPersistentFailure_OnAPermissionResponse(HttpStatusCode statusCode)
    {
        // A permission response (401/403) is a persistent, deterministic fault - it surfaces as unhealthy
        // even for the auto-wired dependency check rather than being softened to a Warning (§3.9).
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(new CapturingHandler(statusCode)), Url);

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.True(result.IsPersistent);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ExecuteAsync_ReturnsFailed_OnAnOtherwiseUnsuccessfulResponse(HttpStatusCode statusCode)
    {
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(new CapturingHandler(statusCode)), Url);

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailed_AndReportsNoMessage_WhenTheTransportThrows()
    {
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(new ThrowingHandler()), Url);

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        // The classified failure reports the exception type, never its (potentially sensitive) message.
        Assert.Equal("HttpRequestException", result.Data["Error"]);
    }

    [Fact]
    public async Task ExecuteAsync_StripsBasicAuthCredentialsFromTheReportedUrlAndDependency()
    {
        var url = "https://user:s3cret@service-b.internal/benzene/invoke";
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(new CapturingHandler(HttpStatusCode.OK)), url);

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.DoesNotContain("s3cret", (string)result.Data["Url"]);
        Assert.Equal("https://service-b.internal/benzene/invoke", result.Data["Url"]);
        Assert.Equal("https://service-b.internal/benzene/invoke", Assert.Single(result.Dependencies).Name);
    }

    // The envelope endpoint answers 200 for every envelope it processed (transport-bindings §1): the
    // verdict is the envelope, read whatever the status line.
    [Theory]
    [InlineData("""{"statusCode":"ok","isSuccessful":true,"headers":{},"body":"{\"isHealthy\":true,\"healthChecks\":{}}"}""", HealthCheckStatus.Ok)]
    [InlineData("""{"statusCode":"ok","isSuccessful":true,"headers":{},"body":"{\"isHealthy\":false,\"healthChecks\":{}}"}""", HealthCheckStatus.Failed)]
    [InlineData("""{"statusCode":"not-found","isSuccessful":false,"headers":{},"body":"{}"}""", HealthCheckStatus.Failed)]
    [InlineData("""{"statusCode":"ok","headers":{},"body":"{\"isHealthy\":true}"}""", HealthCheckStatus.Failed)]
    public async Task ExecuteAsync_ReadsTheEnvelopeAndTheTargetsVerdict_Under200(string envelope, string expected)
    {
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(new CapturingHandler(HttpStatusCode.OK, envelope)), Url);

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_AnUnauthorizedEnvelope_IsAPersistentFailure()
    {
        var check = new HttpBenzeneMessageHealthCheck(new HttpClient(new CapturingHandler(HttpStatusCode.OK,
            """{"statusCode":"unauthorized","isSuccessful":false,"headers":{},"body":"{}"}""")), Url);

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.True(result.IsPersistent);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;

        private readonly string _body;

        public CapturingHandler(HttpStatusCode status, string body = "{}")
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
