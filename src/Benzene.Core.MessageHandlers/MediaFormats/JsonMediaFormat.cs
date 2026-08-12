using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.MediaFormats;
using Benzene.Abstractions.Serialization;
using Benzene.Core.MessageHandlers.Serialization;

namespace Benzene.Core.MessageHandlers.MediaFormats;

/// <summary>
/// The process default <see cref="IMediaFormat{TContext}"/>, wrapping the concrete
/// <see cref="JsonSerializer"/> - deliberately NOT the general <see cref="ISerializer"/> interface,
/// matching the same pattern <c>Benzene.Xml</c>'s <c>XmlMediaFormat</c> uses for
/// <c>Benzene.Xml.XmlSerializer</c>. <see cref="ISerializer"/> is a format-agnostic abstraction -
/// <c>Benzene.Xml.XmlSerializer</c> also implements it - so if this format resolved <see cref="ISerializer"/>
/// instead, a service that replaced <see cref="ISerializer"/> for an unrelated reason (e.g. its
/// outbound message client should serialize XML) would silently make THIS format, which always
/// advertises <see cref="ContentType"/> <c>application/json</c>, render that same XML - a
/// content-type lie. <see cref="JsonSerializer"/> is independently TryAdd-registered by
/// <c>AddBenzene</c>, so to customize JSON rendering specifically (different
/// <see cref="System.Text.Json.JsonSerializerOptions"/>, or a subclass overriding its virtual
/// members) register your own <see cref="JsonSerializer"/> in <c>ConfigureServices</c> - that wins
/// without touching <see cref="ISerializer"/> or any other format.
/// </summary>
/// <remarks>
/// Injected directly into <see cref="MediaFormatNegotiator{TContext}"/> as its fallback rather than
/// registered as one of the negotiated <c>IMediaFormat{TContext}</c> candidates - <see cref="CanRead"/>/<see cref="CanWrite"/>
/// always return <c>false</c> since it's never "discovered" through the matching loop, it's the
/// format used when nothing else matches.
/// </remarks>
/// <typeparam name="TContext">The transport-specific context type this format can apply to.</typeparam>
public class JsonMediaFormat<TContext> : IMediaFormat<TContext>
{
    private readonly JsonSerializer _jsonSerializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonMediaFormat{TContext}"/> class.
    /// </summary>
    /// <param name="jsonSerializer">The JSON serializer this format wraps.</param>
    public JsonMediaFormat(JsonSerializer jsonSerializer)
    {
        _jsonSerializer = jsonSerializer;
    }

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public bool CanRead(TContext context, IServiceResolver serviceResolver) => false;

    /// <inheritdoc />
    public bool CanWrite(TContext context, IServiceResolver serviceResolver) => false;

    /// <inheritdoc />
    public ISerializer GetSerializer(IServiceResolver serviceResolver) => _jsonSerializer;
}
