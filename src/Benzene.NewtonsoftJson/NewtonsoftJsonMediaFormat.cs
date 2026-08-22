using Benzene.Abstractions.DI;
using Benzene.Abstractions.Serialization;
using Benzene.Core.MessageHandlers.MediaFormats;

namespace Benzene.NewtonsoftJson;

/// <summary>
/// Json.NET-backed <see cref="Benzene.Abstractions.MessageHandlers.MediaFormats.IMediaFormat{TContext}"/>:
/// selected to read a request when its <c>content-type</c> is <c>application/json</c>, and to write a
/// response when <c>application/json</c> appears in its <c>accept</c> header - the same negotiation
/// shape as <c>Benzene.Xml</c>'s <c>XmlMediaFormat</c>, just for JSON via Json.NET instead of the
/// process default <see cref="Benzene.Core.MessageHandlers.MediaFormats.JsonMediaFormat{TContext}"/>
/// (which always wraps <c>System.Text.Json</c> and is never one of the negotiated candidates - see its
/// own doc comment). Registering this format lets <c>application/json</c> traffic negotiate to Json.NET
/// semantics instead, without touching the process default.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type this format applies to.</typeparam>
public class NewtonsoftJsonMediaFormat<TContext> : AcceptHeaderMediaFormatBase<TContext>
{
    private readonly JsonSerializer _jsonSerializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewtonsoftJsonMediaFormat{TContext}"/> class.
    /// </summary>
    /// <param name="jsonSerializer">The shared Json.NET serializer this format wraps.</param>
    public NewtonsoftJsonMediaFormat(JsonSerializer jsonSerializer)
    {
        _jsonSerializer = jsonSerializer;
    }

    /// <inheritdoc />
    public override string ContentType => Constants.JsonContentType;

    /// <inheritdoc />
    public override ISerializer GetSerializer(IServiceResolver serviceResolver) => _jsonSerializer;
}
