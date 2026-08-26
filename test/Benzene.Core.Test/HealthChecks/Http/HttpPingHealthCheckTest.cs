using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core;
using Benzene.HealthChecks.Core;
using Benzene.HealthChecks.Http;
using Xunit;

namespace Benzene.Test.HealthChecks.Http;

public class HttpPingHealthCheckTest
{
    [Fact]
    public async Task ExecuteAsync_ObservesCancellation_WhenTheAmbientTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var healthCheck = new HttpPingHealthCheck(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK)),
            "https://example.test/ping", accessor);

        // A cancelled ambient token cancels the request - the exception isolation wrapper turns this
        // into a "Cancelled" result at the processor level; here we assert the check observes it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => healthCheck.ExecuteAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsHealthy_WhenResponseIsOk()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK));
        var healthCheck = new HttpPingHealthCheck(httpClient, "https://example.test/ping");

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("HttpPing", result.Type);
        Assert.Equal("https://example.test/ping", result.Data["Url"]);
        Assert.Equal(HttpStatusCode.OK, result.Data["StatusCode"]);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Http", dependency.Kind);
        Assert.Equal("https://example.test/ping", dependency.Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task ExecuteAsync_ReturnsFailed_WhenResponseIsNotOk(HttpStatusCode statusCode)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(statusCode));
        var healthCheck = new HttpPingHealthCheck(httpClient, "https://example.test/ping");

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal(statusCode, result.Data["StatusCode"]);
    }

    [Fact]
    public async Task ExecuteAsync_StripsBasicAuthCredentialsFromTheReportedUrlAndDependency()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK));
        var healthCheck = new HttpPingHealthCheck(httpClient, "https://user:s3cret@example.test/ping");

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        // Credentials must not leak into the report (it flows out with no authorization).
        Assert.DoesNotContain("s3cret", (string)result.Data["Url"]);
        Assert.Equal("https://example.test/ping", result.Data["Url"]);
        Assert.Equal("https://example.test/ping", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public void Type_IsHttpPing()
    {
        var healthCheck = new HttpPingHealthCheck(new HttpClient(), "https://example.test/ping");

        Assert.Equal("HttpPing", healthCheck.Type);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailed_WithUrlAndDependency_WhenTheEndpointDoesNotRespondAtAll()
    {
        // #112: connection-refused (HttpRequestException) used to escape uncaught into the generic
        // ExceptionHandlingHealthCheck decorator, which builds a result with no Url and no dependency
        // entry - the one failure mode where an operator with several HttpPingHealthChecks registered
        // most needs to know WHICH endpoint is down.
        var httpClient = new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("Connection refused")));
        var healthCheck = new HttpPingHealthCheck(httpClient, "https://example.test/ping");

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal("https://example.test/ping", result.Data["Url"]);
        Assert.Equal("HttpRequestException", result.Data["Exception"]);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Http", dependency.Kind);
        Assert.Equal("https://example.test/ping", dependency.Name);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation_RatherThanReportingItAsAPingFailure()
    {
        // A cancelled token must still propagate uncaught (not be caught by the new HttpRequestException
        // handler added for #112) so ExceptionHandlingHealthCheck classifies it as the distinct
        // "Cancelled" outcome.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK));
        var healthCheck = new HttpPingHealthCheck(httpClient, "https://example.test/ping");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => healthCheck.ExecuteAsync(cts.Token));
    }

    private class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StubHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }

    private class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }
}
