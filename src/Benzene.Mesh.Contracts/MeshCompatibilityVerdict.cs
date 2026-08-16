namespace Benzene.Mesh.Contracts;

/// <summary>
/// The roll-up verdicts a <see cref="MeshTopicCompatibility"/> can carry — loose string constants
/// (the <see cref="MeshServiceStatus"/> convention, not an enum) so an older reader renders an
/// unknown verdict rather than failing to deserialise.
/// </summary>
public static class MeshCompatibilityVerdict
{
    /// <summary>No change detected that the taxonomy classifies as anything worse. Never "safe".</summary>
    public const string Compatible = "compatible";

    /// <summary>At least one change worth surfacing, and none classified breaking.</summary>
    public const string Warning = "warning";

    /// <summary>At least one change that breaks a consumer of the previous version, by the rules in force.</summary>
    public const string Breaking = "breaking";

    /// <summary>
    /// A comparison was attempted and could not be made — see
    /// <see cref="MeshTopicCompatibility.NotComparedReason"/>.
    /// <para>
    /// This is the load-bearing one. It exists so that "we looked and could not tell" is a value on
    /// the wire rather than an absence a render site has to guess at, and so that it can never be
    /// mistaken for <see cref="Compatible"/>. A reader must not paint it green, and must not print a
    /// zero for it.
    /// </para>
    /// </summary>
    public const string NotCompared = "notCompared";
}

/// <summary>
/// Why a <see cref="MeshTopicCompatibility"/> could not produce a verdict. Loose strings, same
/// convention and same reason as <see cref="MeshCompatibilityVerdict"/>.
/// </summary>
public static class MeshNotComparedReason
{
    /// <summary>The catalogue publishes a single version of this topic, so there is no pair to compare.</summary>
    public const string OnlyOneVersion = "onlyOneVersion";

    /// <summary>
    /// Neither version publishes a payload schema on any side, so there was nothing to walk. Distinct
    /// from a side being absent on only one of the two versions, which still permits a partial
    /// comparison and is reported through <see cref="MeshTopicCompatibility.NotComparedSides"/>.
    /// </summary>
    public const string NoSchemaPublished = "noSchemaPublished";
}
