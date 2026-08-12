using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.CodeGen.Cli.Core.Commands.Spec;
using Benzene.Schema.OpenApi;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// HttpSpecSource: --url <base> - GETs {base}/benzene/spec?type=...&format=... (profile R5's spec
// endpoint), mirroring how profile-check's CloudServiceProbe reaches the same endpoint. These tests
// assert the request shape (path/query) is correct and that non-2xx/unreachable responses fail
// loud, without needing a real live URL.
public class HttpSpecSourceTest
{
    [Fact]
    public async Task GetSpecJsonAsync_RequestsTheDerivedSpecEndpoint_WithTypeAndFormatQuery()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"info\":{}}");
        var source = new HttpSpecSource(new HttpClient(handler) { BaseAddress = new Uri("https://orders.example.com") });

        var json = await source.GetSpecJsonAsync(new SpecRequest("benzene", "json"));

        Assert.Equal("{\"info\":{}}", json);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/benzene/spec", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("?type=benzene&format=json", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task GetSpecJsonAsync_DefaultsTypeAndFormat_WhenRequestFieldsAreEmpty()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"info\":{}}");
        var source = new HttpSpecSource(new HttpClient(handler) { BaseAddress = new Uri("https://orders.example.com") });

        await source.GetSpecJsonAsync(new SpecRequest());

        Assert.Equal("?type=benzene&format=json", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task GetSpecJsonAsync_NonSuccessStatus_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "boom");
        var source = new HttpSpecSource(new HttpClient(handler) { BaseAddress = new Uri("https://orders.example.com") });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetSpecJsonAsync(new SpecRequest("benzene", "json")));
        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task GetSpecJsonAsync_UnreachableService_ThrowsWithDiagnosableMessage()
    {
        var source = new HttpSpecSource(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://orders.example.com") });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetSpecJsonAsync(new SpecRequest("benzene", "json")));
        Assert.Contains("did not reach the service", exception.Message);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
