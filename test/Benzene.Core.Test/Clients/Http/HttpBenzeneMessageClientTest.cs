using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Clients.Http;
using Benzene.Clients;
using Benzene.Results;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients.Http;

/// <summary>
/// Coverage for <see cref="HttpBenzeneMessageClient"/> - the client that carries a BenzeneMessage envelope
/// (<c>{ topic, headers, body }</c>) over HTTP to another Benzene service's <c>/benzene/invoke</c> endpoint,
/// the HTTP counterpart of the Lambda invoke path.
/// </summary>
public class HttpBenzeneMessageClientTest
{
    private const string Url = "https://service-b.internal/benzene/invoke";

    [Fact]
    public async Task SendMessageAsync_PostsTheEnvelope_ToTheConfiguredUrl_WithTopicHeadersAndSerializedBody()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"ok","isSuccessful":true,"headers":{},"body":"\"hello\""}""");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var request = new BenzeneClientRequest<string>("my-topic", "the-message",
            new Dictionary<string, string> { { "tenant", "acme" } });

        await client.SendMessageAsync<string, string>(request);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(Url, handler.LastRequest.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("my-topic", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("acme", doc.RootElement.GetProperty("headers").GetProperty("tenant").GetString());
        // The body is the serialized message payload (a JSON string here), nested inside the envelope.
        Assert.Equal("\"the-message\"", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task SendMessageAsync_MapsASuccessEnvelope_ToAnOkResultWithThePayload()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"ok","isSuccessful":true,"headers":{},"body":"\"hello\""}""");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Equal("hello", result.Payload);
    }

    [Fact]
    public async Task SendMessageAsync_MapsTheEnvelopeStatus_WhateverTheHttpStatus()
    {
        // The envelope in the body is the outcome whatever the status line (transport-bindings §1): a
        // conformant endpoint answers 200 for a processed not-found, an older one 404 - both read alike.
        var handler = new CapturingHandler(HttpStatusCode.NotFound, """{"statusCode":"not-found","isSuccessful":false,"headers":{},"body":null}""");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.NotFound, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task SendMessageAsync_ForVoidResponse_MapsStatusWithoutDeserializingABody()
    {
        // A Void (send-acknowledgement) caller: the handler's body shape is undefined, so we map the status
        // only. A non-envelope-shaped body must not break the Void path.
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"ok","isSuccessful":true,"headers":{},"body":"anything at all"}""");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, Void>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public async Task SendMessageAsync_ReturnsServiceUnavailable_WhenTheTransportThrows()
    {
        var client = new HttpBenzeneMessageClient(new HttpClient(new ThrowingHandler()), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ReturnsServiceUnavailable_WhenTheTransportThrows_AndTheLoggerIsNull()
    {
        // #192: HttpBenzeneMessageClient's logger parameter is optional (null already meant "no
        // logging" via its own `?.` calls), but the constructor now normalizes a null logger to
        // NullLogger.Instance too, for consistency with the other nine BenzeneMessageClient classes
        // (P8) - confirm the failure path still returns cleanly rather than throwing.
        var client = new HttpBenzeneMessageClient(new HttpClient(new ThrowingHandler()), Url, logger: null);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ProcessedFailureEnvelopeUnder200_IsTheEnvelopesFailure()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"conflict","isSuccessful":false,"headers":{},"body":null}""");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.Conflict, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task SendMessageAsync_AnEnvelopeWithoutIsSuccessful_IsUnsuccessful()
    {
        // wire-contracts §1.2: an envelope that omits isSuccessful is malformed - never classified from text.
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"ok","headers":{},"body":"\"hello\""}""");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.False(result.IsSuccessful);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "<html>sign in</html>", BenzeneResultStatus.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadGateway, "Bad Gateway", BenzeneResultStatus.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", BenzeneResultStatus.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.OK, "", BenzeneResultStatus.Ok, true)]
    public async Task SendMessageAsync_ABodyThatIsNotAnEnvelope_FallsBackToTheReverseHttpTable(
        HttpStatusCode httpStatus, string body, string expectedStatus, bool expectedSuccess)
    {
        var handler = new CapturingHandler(httpStatus, body);
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedSuccess, result.IsSuccessful);
    }

    // A failure envelope whose body is a problem document carrying field/code errors and an app-defined type.
    private const string FailureEnvelopeWithProblem = """
        {"statusCode":"validation-error","isSuccessful":false,"headers":{"content-type":"application/problem+json"},
         "body":"{\"type\":\"https://example.com/problems/custom-rule\",\"benzeneStatus\":\"validation-error\",\"detail\":\"Custom rule failed\",\"errors\":[{\"message\":\"Custom rule failed\",\"field\":\"/sku\",\"code\":\"CustomRule\"}]}"}
        """;

    [Fact]
    public async Task SendMessageAsync_AFailureEnvelopesProblemDocument_RebuildsErrorsWithFieldAndCode()
    {
        // core-concepts §12, error fidelity (wire-contracts §1.3).
        var client = new HttpBenzeneMessageClient(new HttpClient(new CapturingHandler(HttpStatusCode.OK, FailureEnvelopeWithProblem)), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.ValidationError, result.Status);
        Assert.Equal(new[] { new BenzeneError("Custom rule failed", "/sku", "CustomRule") }, result.Errors);
        Assert.Equal("https://example.com/problems/custom-rule", result.GetProblem().Type);
    }

    [Fact]
    public async Task SendMessageAsync_ForVoidResponse_AFailureStillCarriesItsErrors()
    {
        // The Void path skips only a success body; a failure's body is a problem document whatever the
        // caller's TResponse, so its errors are rebuilt too.
        var client = new HttpBenzeneMessageClient(new HttpClient(new CapturingHandler(HttpStatusCode.OK, FailureEnvelopeWithProblem)), Url);

        var result = await client.SendMessageAsync<string, Void>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.ValidationError, result.Status);
        Assert.Equal(new[] { new BenzeneError("Custom rule failed", "/sku", "CustomRule") }, result.Errors);
    }

    [Fact]
    public async Task SendMessageAsync_ANonEnvelopeProblemJsonFailure_IsReadAsAProblemDocument()
    {
        // A host or gateway answering with its own problem document before any envelope was read: the
        // reverse HTTP table classifies it, and the document's errors are rebuilt.
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized,
            """{"type":"https://idp.example/problems/expired","status":401,"detail":"Token expired","errors":[{"message":"Token expired","code":"TokenExpired"}]}""",
            "application/problem+json");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.Unauthorized, result.Status);
        Assert.Equal(new[] { new BenzeneError("Token expired", code: "TokenExpired") }, result.Errors);
        Assert.Equal("https://idp.example/problems/expired", result.GetProblem().Type);
    }

    [Fact]
    public async Task SendMessageAsync_ANonEnvelopeProblemDocumentsBenzeneStatus_WinsOverTheReverseHttpTable()
    {
        // wire-contracts §4.1 (spec 1.1): no envelope carries the status here, so the problem document's
        // benzeneStatus is the result's status rather than the reverse table's (500 -> unexpected-error).
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError,
            """{"benzeneStatus":"insufficient-funds","detail":"Balance too low"}""", "application/problem+json");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal("insufficient-funds", result.Status);
        Assert.False(result.IsSuccessful);
        Assert.Equal("Balance too low", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task SendMessageAsync_InsideAnEnvelope_TheEnvelopesStatusCodeStaysAuthoritative()
    {
        // Envelope-first (wire-contracts §1.2/§1.3): the document's benzeneStatus mirrors the envelope's
        // statusCode, and where they disagree the envelope wins.
        const string envelope = """
            {"statusCode":"conflict","isSuccessful":false,
             "body":"{\"benzeneStatus\":\"validation-error\",\"detail\":\"Already exists\"}"}
            """;
        var client = new HttpBenzeneMessageClient(new HttpClient(new CapturingHandler(HttpStatusCode.OK, envelope)), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.Conflict, result.Status);
        Assert.Equal("Already exists", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task SendMessageAsync_ANonEnvelopeFailureThatIsNotProblemJson_CarriesNoErrors()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadGateway, """{"message":"upstream down"}""");
        var client = new HttpBenzeneMessageClient(new HttpClient(handler), Url);

        var result = await client.SendMessageAsync<string, string>(
            new BenzeneClientRequest<string>("t", "m", new Dictionary<string, string>()));

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Empty(result.Errors);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _mediaType;
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;

        public CapturingHandler(HttpStatusCode status, string body, string mediaType = "application/json")
        {
            _status = status;
            _body = body;
            _mediaType = mediaType;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, _mediaType) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
