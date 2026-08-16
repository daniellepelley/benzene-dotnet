namespace Benzene.Mesh.Contracts;

/// <summary>
/// One classified difference between two versions of a topic's payload contract — which named field
/// moved, on which side of the message, and how the rules in force classified it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately five loose strings rather than enums or a reference to the classifier. This type
/// crosses the wire into <c>topics.json</c> and is mirrored by every port that builds a catalogue, so
/// it must stay free of the OpenAPI toolchain the .NET classifier happens to sit beside, and a reader
/// on an older build must be able to render a kind it has never heard of rather than fail to
/// deserialise. Same reasoning as <see cref="MeshTopicChange"/>'s loose <c>kind</c>.
/// </para>
/// <para>
/// A consumer that wants the strong types can map <see cref="Kind"/>, <see cref="Direction"/> and
/// <see cref="Compatibility"/> back through <c>Benzene.Schema.Compatibility</c>; the canonical
/// spellings are the camel-cased enum names, e.g. <c>propertyRemoved</c> / <c>request</c> /
/// <c>breaking</c>.
/// </para>
/// </remarks>
public class MeshSchemaChange
{
    /// <summary>Initializes a new instance of the <see cref="MeshSchemaChange"/> class.</summary>
    /// <param name="kind">The kind of change — see <see cref="Kind"/>.</param>
    /// <param name="direction">Which side of the message changed — see <see cref="Direction"/>.</param>
    /// <param name="path">A dotted path to the changed field — see <see cref="Path"/>.</param>
    /// <param name="description">A human-readable sentence describing the change.</param>
    /// <param name="compatibility">How the rules in force classified it — see <see cref="Compatibility"/>.</param>
    public MeshSchemaChange(string kind, string direction, string path, string description, string compatibility)
    {
        Kind = kind;
        Direction = direction;
        Path = path;
        Description = description;
        Compatibility = compatibility;
    }

    /// <summary>
    /// The kind of change, camel-cased: <c>propertyAdded</c>, <c>requiredPropertyAdded</c>,
    /// <c>propertyRemoved</c>, <c>propertyBecameRequired</c>, <c>propertyBecameOptional</c>,
    /// <c>typeChanged</c>. A reader must tolerate a kind it does not recognise.
    /// </summary>
    public string Kind { get; }

    /// <summary>Which side of the message: <c>request</c>, <c>response</c> or <c>event</c>.</summary>
    public string Direction { get; }

    /// <summary>
    /// A dotted path to the changed field, prefixed with the topic and side, e.g.
    /// <c>orders:create.request.customerId</c>. Array elements appear as <c>[]</c>. This is the field
    /// a reader acts on, and the key a UI uses to annotate the schema tree in place.
    /// </summary>
    public string Path { get; }

    /// <summary>A human-readable sentence, e.g. <c>Property 'customerId' was removed</c>.</summary>
    public string Description { get; }

    /// <summary>
    /// <c>compatible</c>, <c>warning</c> or <c>breaking</c>, <b>by the rules in force when the
    /// aggregator ran</b>. Those rules are configurable, so this is a function of a rule table rather
    /// than a fact about the world, and any surface that shows it must attribute it.
    /// </summary>
    public string Compatibility { get; }
}
