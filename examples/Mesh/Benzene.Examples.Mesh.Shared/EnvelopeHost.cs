using System.Text;
using System.Text.Json;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.Mesh.Shared;

/// <summary>
/// Hosts a set of message handlers behind a wire-envelope HTTP endpoint
/// (<c>POST {topic, headers, body}</c> in, <c>{statusCode, headers, body}</c> out) - the
/// service-to-service surface the mesh speaks. The aggregator host uses it to serve the
/// <c>Benzene.Mesh.Collector</c> handlers; the services themselves are meshed by
/// <c>UseBenzeneCloudService</c> (registration of their contract document, heartbeats, traces).
/// </summary>
public sealed class EnvelopeHost
{
    private readonly BenzeneMessageApplication _application;
    private readonly Abstractions.DI.IServiceResolverFactory _resolverFactory;

    public EnvelopeHost(Type[] handlerTypes, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configureServices?.Invoke(services);

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineBuilder.UseMessageHandlers(handlerTypes);

        _application = new BenzeneMessageApplication(pipelineBuilder.Build());
        _resolverFactory = container.CreateServiceResolverFactory();
    }

    /// <summary>Adapts an ASP.NET request to the envelope pipeline. Map it with
    /// <c>endpoints.MapPost("/benzene/invoke", host.HandleAsync)</c>.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var envelope = JsonSerializer.Deserialize<Envelope>(await reader.ReadToEndAsync(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Envelope();

        var response = await _application.HandleAsync(new BenzeneMessageRequest
        {
            Topic = envelope.Topic,
            Headers = envelope.Headers ?? new Dictionary<string, string>(),
            Body = envelope.Body ?? string.Empty
        }, _resolverFactory);

        context.Response.ContentType = "application/json";
        // isSuccessful is required (wire-contracts §1.2): a sender reads it as the success signal - the
        // cloud service's announcer retried every registration while it was missing here.
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            statusCode = response.StatusCode,
            isSuccessful = response.IsSuccessful,
            headers = response.Headers ?? new Dictionary<string, string>(),
            body = response.Body ?? string.Empty
        }));
    }

    private class Envelope
    {
        public string Topic { get; set; } = string.Empty;
        public Dictionary<string, string>? Headers { get; set; }
        public string? Body { get; set; }
    }
}

/// <summary>
/// A tiny envelope-speaking client for cross-service calls in the examples, forwarding the
/// current mesh span as a <c>traceparent</c> header so the collector can observe (mesh.md §4.2 -
/// liveness/drift, additive to the declared graph) which of a service's declared edges are actually
/// being exercised.
/// </summary>
public static class EnvelopeClient
{
    private static readonly HttpClient Http = new();

    public static async Task<(string StatusCode, string Body)> SendAsync(string envelopeUrl, string topic, string body)
    {
        var headers = new Dictionary<string, string>();
        if (MeshSpan.Current is { } span)
        {
            headers["traceparent"] = span.ToTraceparent();
        }
        var envelope = JsonSerializer.Serialize(new { topic, headers, body });
        using var content = new StringContent(envelope, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(envelopeUrl, content);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            document.RootElement.GetProperty("statusCode").GetString() ?? string.Empty,
            document.RootElement.GetProperty("body").GetString() ?? string.Empty);
    }
}
