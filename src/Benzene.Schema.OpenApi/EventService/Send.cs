using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;

namespace Benzene.Schema.OpenApi.EventService;

/// <summary>
/// A <c>sends[]</c> entry (contract-document.md §3.2): a message this service sends to a topic
/// <b>another party</b> defines - a command to a queue, a request to another service, a publish onto
/// someone else's topic. A dependency of this service, which a client of it cannot observe; the field
/// names mirror <see cref="RequestResponse"/> because a send is the client-side image of someone
/// else's request.
/// </summary>
public class Send : IOpenApiSerializable
{
    /// <summary>Initializes a send entry.</summary>
    /// <param name="topic">The target topic id.</param>
    /// <param name="request">The schema of the message sent.</param>
    /// <param name="response">The schema of the reply; null for one-way (nothing comes back).</param>
    public Send(string topic, OpenApiSchema request, OpenApiSchema? response = null)
    {
        Topic = topic;
        Request = request;
        Response = response;
    }

    /// <summary>The target topic id.</summary>
    public string Topic { get; }

    /// <summary>The version the sender writes into <c>benzene-version</c>; empty when unversioned (and then not written).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The schema of the message sent.</summary>
    public OpenApiSchema Request { get; }

    /// <summary>The schema of the reply. <b>Null (absent) means one-way</b>; <c>{}</c> means a reply of unconstrained shape.</summary>
    public OpenApiSchema? Response { get; }

    /// <inheritdoc />
    public void SerializeAsV3(IOpenApiWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteProperty("topic", Topic);
        if (!string.IsNullOrEmpty(Version))
        {
            writer.WriteProperty("version", Version);
        }

        writer.WriteRequiredObject("request", Request, (w, o) => o.SerializeAsV3(w));
        if (Response != null)
        {
            writer.WriteRequiredObject("response", Response, (w, o) => o.SerializeAsV3(w));
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public void SerializeAsV2(IOpenApiWriter writer)
    {
        SerializeAsV3(writer);
    }
}
