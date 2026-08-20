using System.Text.Json.Nodes;

namespace Benzene.Mesh.Contracts;

/// <summary>
/// What one service declares a topic's payload looks like — its own declaration, verbatim, attributed
/// to it by name.
/// </summary>
/// <remarks>
/// <para>
/// Published only where <see cref="MeshTopicEntry.SchemaMismatch"/> is true, and it exists for exactly
/// one reason: the flag says the services on a topic disagree about its shape, and until now nothing
/// said <em>where</em>. A reader was told two services will fail to talk to each other and then left to
/// open each service's own spec by hand. That is a detection with no finding underneath it.
/// </para>
/// <para>
/// <b>Raw declarations, not a computed diff.</b> A diff needs a baseline, and choosing one crowns a
/// service as the reference — "billing-api is missing customerId" is a verdict nobody earned, since
/// either declaration could be the correct one. Publishing what each service actually declared keeps
/// the data symmetric by construction, and lets a reader compare on any axis they care about rather
/// than the one axis a comparer happened to classify. It also carries differences that a
/// keyword-limited comparer structurally cannot see — a tightened <c>maxLength</c>, a changed
/// <c>pattern</c> — which would otherwise be published as an empty difference list.
/// </para>
/// <para>
/// Absence is not agreement: a service that declared no schema for a side is present here with a null
/// for that side, and a service missing entirely contributed no declaration at all.
/// </para>
/// </remarks>
public class MeshDeclaredSchema
{
    /// <summary>Initializes a new instance of the <see cref="MeshDeclaredSchema"/> class.</summary>
    /// <param name="service">The declaring service.</param>
    /// <param name="role">One of <see cref="MeshDeclaredSchemaRole"/>.</param>
    /// <param name="requestSchema">The inbound payload this service declares it accepts, or null.</param>
    /// <param name="responseSchema">The response payload this service declares it returns, or null.</param>
    /// <param name="messageSchema">The message this service declares it sends, or null.</param>
    public MeshDeclaredSchema(string service, string role,
        JsonObject? requestSchema = null, JsonObject? responseSchema = null, JsonObject? messageSchema = null)
    {
        Service = service;
        Role = role;
        RequestSchema = requestSchema;
        ResponseSchema = responseSchema;
        MessageSchema = messageSchema;
    }

    /// <summary>The service whose declaration this is. Never inferred — this is the attribution the
    /// flag was missing.</summary>
    public string Service { get; }

    /// <summary>
    /// One of <see cref="MeshDeclaredSchemaRole"/>: whether this service handles the topic or sends it.
    /// A reader must tolerate a role it does not recognise, per this catalogue's loose-string
    /// convention.
    /// </summary>
    public string Role { get; }

    /// <summary>The inbound payload this service declares it accepts. Null means it declared none —
    /// no signal, never agreement.</summary>
    public JsonObject? RequestSchema { get; }

    /// <summary>The response payload this service declares it returns, or null when it declared none.</summary>
    public JsonObject? ResponseSchema { get; }

    /// <summary>The message this service declares it sends, or null. Only meaningful for a producer.</summary>
    public JsonObject? MessageSchema { get; }
}

/// <summary>
/// Which end of a topic a <see cref="MeshDeclaredSchema"/> comes from — loose string constants (the
/// <see cref="MeshTopicStatus"/> convention, not an enum) so an older reader renders an unknown role
/// rather than failing to deserialise.
/// </summary>
public static class MeshDeclaredSchemaRole
{
    /// <summary>The service handles this topic — its request/response declarations apply.</summary>
    public const string Consumer = "consumer";

    /// <summary>The service sends this topic — its message declaration applies.</summary>
    public const string Producer = "producer";
}
