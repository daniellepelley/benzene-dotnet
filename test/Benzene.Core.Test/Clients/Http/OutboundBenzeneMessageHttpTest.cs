using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.Http;
using Benzene.HealthChecks.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients.Http;

/// <summary>
/// Covers <c>UseBenzeneMessageOverHttp</c> - the <c>OutboundContext</c> binding for
/// <see cref="HttpBenzeneMessageClient"/>'s envelope-over-HTTP shape, so an outbound route
/// (<c>AddOutboundRouting(...).Route(...)</c>) can reach another Benzene service over plain HTTP
/// without a hand-written terminal middleware.
/// </summary>
public class OutboundBenzeneMessageHttpTest
{
    private const string Url = "https://service-b.internal/benzene/invoke";

    private static (IBenzeneMessageSender Sender, IServiceProvider Provider) Build(
        HttpMessageHandler handler, Action<OutboundRoutingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient(handler));
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(configure);
        var provider = services.BuildServiceProvider();
        return (new MicrosoftServiceResolverAdapter(provider).GetService<IBenzeneMessageSender>(), provider);
    }

    [Fact]
    public async Task SendAsync_PostsTheEnvelope_ToTheConfiguredUrl_WithTopicHeadersAndSerializedBody()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"ok","isSuccessful":true,"headers":{},"body":"{\"id\":42,\"name\":\"foo\"}"}""");
        var (sender, _) = Build(handler, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        await sender.SendAsync<ExampleRequestPayload, ExampleResponsePayload>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" },
            new Dictionary<string, string> { { "tenantId", "tenant-1" } });

        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(Url, handler.LastRequest.RequestUri.ToString());

        using var doc = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal(Defaults.Topic, doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("tenant-1", doc.RootElement.GetProperty("headers").GetProperty("tenantId").GetString());
        Assert.Contains("\"foo\"", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task SendAsync_MapsASuccessEnvelope_ToATypedResult()
    {
        // The point of the envelope shape over the fire-and-forget transports: the route can return a
        // typed response, because DefaultBenzeneMessageSender deserializes the raw envelope once it
        // knows TResponse.
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"ok","isSuccessful":true,"headers":{},"body":"{\"name\":\"bar\"}"}""");
        var (sender, _) = Build(handler, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        var result = await sender.SendAsync<ExampleRequestPayload, ExampleResponsePayload>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Equal("bar", result.Payload.Name);
    }

    [Fact]
    public async Task SendAsync_MapsTheEnvelopeStatus_EvenWhenTheHttpStatusIsNon2xx()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound, """{"statusCode":"not-found","isSuccessful":false,"headers":{},"body":null}""");
        var (sender, _) = Build(handler, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.Equal(BenzeneResultStatus.NotFound, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task SendAsync_ABodyThatIsNotAnEnvelope_FallsBackToTheReverseHttpTable()
    {
        // transport-bindings §1: only a non-envelope body lets the HTTP status decide (503 -> service-unavailable).
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable, "");
        var (sender, _) = Build(handler, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendAsync_AFailureEnvelopesProblemDocument_RebuildsErrorsWithFieldAndCode()
    {
        // core-concepts §12, error fidelity - for a Void caller too, since a failure's body is a problem document.
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"statusCode":"validation-error","isSuccessful":false,"headers":{},"body":"{\"type\":\"https://benzene.app/problems/validation-error\",\"benzeneStatus\":\"validation-error\",\"errors\":[{\"message\":\"Name must not be empty\",\"field\":\"/name\",\"code\":\"NotEmptyValidator\"}]}"}""");
        var (sender, _) = Build(handler, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "" });

        Assert.Equal(BenzeneResultStatus.ValidationError, result.Status);
        Assert.Equal(new[] { new BenzeneError("Name must not be empty", "/name", "NotEmptyValidator") }, result.Errors);
    }

    [Fact]
    public async Task SendAsync_ANonEnvelopeProblemJsonFailure_RebuildsItsErrors()
    {
        var handler = new CapturingHandler(HttpStatusCode.Forbidden,
            """{"type":"https://gateway.example/problems/denied","status":403,"errors":[{"message":"Scope missing","code":"ScopeMissing"}]}""",
            "application/problem+json");
        var (sender, _) = Build(handler, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        var result = await sender.SendAsync<ExampleRequestPayload, ExampleResponsePayload>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.Equal(BenzeneResultStatus.Forbidden, result.Status);
        Assert.Equal(new[] { new BenzeneError("Scope missing", code: "ScopeMissing") }, result.Errors);
        Assert.Equal("https://gateway.example/problems/denied", result.GetProblem().Type);
    }

    [Fact]
    public async Task SendAsync_ANonEnvelopeProblemDocumentsBenzeneStatus_IsTheResultsStatus()
    {
        // wire-contracts §4.1 (spec 1.1): outside an envelope, benzeneStatus wins over the reverse table.
        var handler = new CapturingHandler(HttpStatusCode.Forbidden,
            """{"status":403,"benzeneStatus":"tenant-suspended","errors":[{"message":"Tenant suspended"}]}""",
            "application/problem+json");
        var (sender, _) = Build(handler, routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        var result = await sender.SendAsync<ExampleRequestPayload, ExampleResponsePayload>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.Equal("tenant-suspended", result.Status);
        Assert.False(result.IsSuccessful);
        Assert.Equal("Tenant suspended", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task SendAsync_ThroughTheExplicitInnerPipelineOverload_UsesTheGivenHttpClient()
    {
        // The rung below the default: the caller configures the inner send pipeline and hands over the
        // HttpClient explicitly rather than having it resolved from the container.
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"statusCode":"ok","isSuccessful":true,"headers":{},"body":null}""");
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url,
                builder => builder.UseHttpClient(new HttpClient(handler)))));
        var sender = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public void UseBenzeneMessageOverHttp_AutoRegistersADependencyHealthCheckForTheTarget()
    {
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url)));

        Assert.Contains(services, x => x.ServiceType == typeof(IDependencyHealthCheck));
    }

    [Fact]
    public void UseBenzeneMessageOverHttp_WithHealthCheckFalse_RegistersNoHealthCheck()
    {
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseBenzeneMessageOverHttp(Url, healthCheck: false)));

        Assert.DoesNotContain(services, x => x.ServiceType == typeof(IDependencyHealthCheck));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _mediaType;
        public HttpRequestMessage LastRequest;
        public string LastRequestBody;

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
}
