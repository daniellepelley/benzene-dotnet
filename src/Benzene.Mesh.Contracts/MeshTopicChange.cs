namespace Benzene.Mesh.Contracts;

/// <summary>
/// One detected difference for a (topic, version) between the previous aggregator run's
/// <c>topics.json</c> and this one - the topic-level "what changed" substance a plain
/// contract-drift hash can't give. Computed by <c>Benzene.Mesh.Aggregator</c> from its own
/// previous artifact; never a claim any service makes about itself.
/// </summary>
public class MeshTopicChange
{
    /// <summary>Initializes a new instance of the <see cref="MeshTopicChange"/> class.</summary>
    /// <param name="kind">One of <see cref="MeshTopicChangeKind"/>.</param>
    /// <param name="description">A human-readable description of the change.</param>
    /// <param name="schemaChanges">The field-level breakdown, for a <see cref="MeshTopicChangeKind.SchemaChanged"/> change — see <see cref="SchemaChanges"/>.</param>
    /// <param name="compatibility">The roll-up verdict over <paramref name="schemaChanges"/> — see <see cref="Compatibility"/>.</param>
    public MeshTopicChange(string kind, string description,
        MeshSchemaChange[]? schemaChanges = null, string? compatibility = null)
    {
        Kind = kind;
        Description = description;
        SchemaChanges = schemaChanges;
        Compatibility = compatibility;
    }

    /// <summary>One of <see cref="MeshTopicChangeKind"/>.</summary>
    public string Kind { get; }

    /// <summary>A human-readable description of the change.</summary>
    public string Description { get; }

    /// <summary>
    /// For a <see cref="MeshTopicChangeKind.SchemaChanged"/> change: which named fields moved between
    /// the previous run's schema and this one, each already classified — the same
    /// <see cref="MeshSchemaChange"/> shape, and the same classifier, that
    /// <see cref="MeshTopicCompatibility.Changes"/> uses version-over-version.
    /// <para>
    /// This exists because "Payload schema changed (request)" is a <em>detection</em> rendered as a
    /// <em>finding</em>: it says something moved and declines to say what, or whether it breaks
    /// anybody. A reader who cannot get from a drift signal to a named field and a verdict cannot act
    /// on it, and goes back to diffing spec documents by hand.
    /// </para>
    /// <para>
    /// <c>null</c> on any other kind, and on a <c>schema-changed</c> published by a build or a port
    /// that does not classify — which is <b>not</b> the same as "nothing was classifiable". An empty
    /// array is that: the two schemas differ textually (a description, an example, a <c>minimum</c>)
    /// but nothing the taxonomy is defined over moved.
    /// </para>
    /// </summary>
    public MeshSchemaChange[]? SchemaChanges { get; }

    /// <summary>
    /// The worst <see cref="MeshCompatibilityVerdict"/> across <see cref="SchemaChanges"/>, or
    /// <c>null</c> when there is nothing to roll up. Carries the same attribution caveat as every
    /// other verdict in this catalogue: it is a function of the rule table in force when the
    /// aggregator ran, not a fact about the world.
    /// </summary>
    public string? Compatibility { get; }
}
