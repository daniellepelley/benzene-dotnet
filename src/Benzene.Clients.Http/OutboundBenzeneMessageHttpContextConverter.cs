using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Clients.Http;

/// <summary>
/// Converts between an outbound <see cref="OutboundContext"/> and an <see cref="HttpSendMessageContext"/>
/// carrying a BenzeneMessage envelope (<c>{ topic, headers, body }</c>), so an outbound route
/// (<c>OutboundRoutingBuilder.Route</c>) can reach another Benzene service over ordinary HTTP. The
/// <see cref="OutboundContext"/> counterpart of <see cref="HttpBenzeneMessageClient"/>, and the same
/// shape every other transport's outbound converter follows (see
/// <c>Benzene.Clients.InProcess.InProcessContextConverter</c>).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="HttpContextConverter{TRequest,TResponse}"/>/<c>UseHttp(verb, path)</c> in this same
/// package - which makes a plain REST call to a verb+path - this converter speaks the BenzeneMessage
/// <em>envelope</em>, so the receiving side routes on the envelope's topic exactly as it would for a
/// queue or a Lambda invoke, and one endpoint serves every topic.
/// </para>
/// <para>
/// Response mapping is deliberately untyped: the target's response envelope is set on
/// <see cref="OutboundContext.Response"/> as a raw <see cref="BenzeneMessageClientResponse"/>, the same
/// envelope every message-client transport hands back. <c>DefaultBenzeneMessageSender</c> deserializes
/// it into the caller's requested <c>TResponse</c> once it knows that type - so unlike SQS/Kafka/RabbitMQ,
/// a route terminating here can return a typed response, not just <c>Void</c>.
/// </para>
/// </remarks>
public class OutboundBenzeneMessageHttpContextConverter : IContextConverter<OutboundContext, HttpSendMessageContext>
{
    private readonly string _url;
    private readonly ISerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundBenzeneMessageHttpContextConverter"/> class
    /// using a <see cref="JsonSerializer"/> to serialize the envelope and its body.
    /// </summary>
    /// <param name="url">The target BenzeneMessage endpoint URL (used verbatim as the request URI; absolute, or relative to the <c>HttpClient</c>'s <c>BaseAddress</c>).</param>
    public OutboundBenzeneMessageHttpContextConverter(string url)
        : this(url, new JsonSerializer())
    { }

    /// <summary>Initializes a new instance of the <see cref="OutboundBenzeneMessageHttpContextConverter"/> class.</summary>
    /// <param name="url">The target BenzeneMessage endpoint URL.</param>
    /// <param name="serializer">The serializer used for the envelope and the message body.</param>
    public OutboundBenzeneMessageHttpContextConverter(string url, ISerializer serializer)
    {
        _url = url;
        _serializer = serializer;
    }

    /// <summary>Builds the POST of the BenzeneMessage envelope to the configured URL.</summary>
    /// <param name="contextIn">The outbound context to convert.</param>
    /// <returns>A task that resolves to the built <see cref="HttpSendMessageContext"/>.</returns>
    public Task<HttpSendMessageContext> CreateRequestAsync(OutboundContext contextIn)
    {
        var envelope = new BenzeneMessageEnvelope
        {
            Topic = contextIn.Topic,
            Headers = contextIn.Headers,
            Body = _serializer.Serialize(contextIn.Request)
        };

        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            // RelativeOrAbsolute so a relative URL composes with the HttpClient's BaseAddress instead
            // of throwing; an absolute URL still works unchanged.
            RequestUri = new Uri(_url, UriKind.RelativeOrAbsolute),
            Content = new StringContent(_serializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };

        return Task.FromResult(new HttpSendMessageContext(request));
    }

    /// <summary>
    /// Reads the target's response envelope and sets it on the outbound context as a raw
    /// <see cref="BenzeneMessageClientResponse"/>.
    /// </summary>
    /// <param name="contextIn">The outbound context to set the response on.</param>
    /// <param name="contextOut">The completed <see cref="HttpSendMessageContext"/>.</param>
    /// <returns>A task that completes once the response body has been read.</returns>
    public async Task MapResponseAsync(OutboundContext contextIn, HttpSendMessageContext contextOut)
    {
        // Both HttpRequestMessage and HttpResponseMessage are IDisposable and created per outbound
        // call; dispose them once the body has been read - matching HttpContextConverter.
        contextOut.Request?.Dispose();
        using var httpResponse = contextOut.Response;
        var body = await httpResponse.Content.ReadAsStringAsync();

        // The envelope carries the authoritative Benzene status in its own body; the target maps that
        // onto the HTTP status too, so a mapped non-2xx (e.g. 404 for not-found) is a normal result,
        // not a transport failure. Map the envelope regardless of the HTTP status code.
        var envelope = string.IsNullOrWhiteSpace(body)
            ? null
            : _serializer.Deserialize<BenzeneMessageClientResponse>(body);

        // An empty/blank body is not a BenzeneMessage envelope - the target returned nothing to map.
        // Surface it as a service-unavailable envelope rather than a null dereference downstream.
        contextIn.Response = envelope ?? new BenzeneMessageClientResponse(
            BenzeneResultStatus.ServiceUnavailable,
            null!,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            false);
    }
}
