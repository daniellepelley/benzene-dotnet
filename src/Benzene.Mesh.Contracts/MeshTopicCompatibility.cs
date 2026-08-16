namespace Benzene.Mesh.Contracts;

/// <summary>
/// The result of comparing one version of a topic's payload contract against the version published
/// before it, in the same catalogue — <em>"does this new version break a consumer still on the old
/// one?"</em>
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>cross-version</b> comparison inside a single run, which is a different question from
/// <see cref="MeshTopicEntry.Changes"/> — that one compares this run against the previous run.
/// A reader must never conflate them: "changed against v1" and "changed since yesterday" lead to
/// opposite actions.
/// </para>
/// <para>
/// What it can see: published payload schemas. What it cannot see, ever: upcasters on the consumer,
/// what a field <em>means</em>, and consumers outside this estate. A verdict of <c>compatible</c>
/// therefore never means safe, and every surface that renders one is obliged to say so.
/// </para>
/// </remarks>
public class MeshTopicCompatibility
{
    /// <summary>Initializes a new instance of the <see cref="MeshTopicCompatibility"/> class.</summary>
    /// <param name="baselineVersion">The version compared against, or <c>null</c> when there was none.</param>
    /// <param name="overall">The roll-up verdict — see <see cref="Overall"/>.</param>
    /// <param name="changes">Every classified change, in traversal order.</param>
    /// <param name="notComparedReason">Why no comparison happened — see <see cref="NotComparedReason"/>.</param>
    /// <param name="truncatedPaths">Paths where a type change stopped the walk — see <see cref="TruncatedPaths"/>.</param>
    /// <param name="notComparedSides">Sides that could not be compared — see <see cref="NotComparedSides"/>.</param>
    public MeshTopicCompatibility(string? baselineVersion, string overall, MeshSchemaChange[]? changes = null,
        string? notComparedReason = null, string[]? truncatedPaths = null, string[]? notComparedSides = null)
    {
        BaselineVersion = baselineVersion;
        Overall = overall;
        Changes = changes ?? Array.Empty<MeshSchemaChange>();
        NotComparedReason = notComparedReason;
        TruncatedPaths = truncatedPaths ?? Array.Empty<string>();
        NotComparedSides = notComparedSides ?? Array.Empty<string>();
    }

    /// <summary>
    /// The version this one was compared against — the immediately preceding published version of the
    /// same topic. <c>null</c> when <see cref="Overall"/> is <c>notCompared</c>.
    /// </summary>
    public string? BaselineVersion { get; }

    /// <summary>
    /// One of <c>compatible</c>, <c>warning</c>, <c>breaking</c> or <c>notCompared</c>.
    /// <para>
    /// <c>notCompared</c> is a <b>value, not an absence</b>, and that is the point of it: the decision
    /// that no verdict could be earned is made once, here, rather than left to a judgement call at
    /// every render site. It is never <c>ok</c>, never a green tick, and never blank.
    /// </para>
    /// </summary>
    public string Overall { get; }

    /// <summary>
    /// Every classified change, in traversal order. Empty when <see cref="Overall"/> is
    /// <c>compatible</c> with nothing detected, and also when it is <c>notCompared</c> — which is
    /// exactly why a reader must branch on <see cref="Overall"/> and not on this being empty.
    /// </summary>
    public MeshSchemaChange[] Changes { get; }

    /// <summary>
    /// Why no comparison was made, when <see cref="Overall"/> is <c>notCompared</c>: one of
    /// <c>onlyOneVersion</c>, <c>noSchemaPublished</c>. <c>null</c> otherwise.
    /// </summary>
    public string? NotComparedReason { get; }

    /// <summary>
    /// Paths at which a type change stopped the walk. The comparer does not diff the members of two
    /// fundamentally different types, so any change beneath one of these paths is <b>invisible</b> and
    /// <see cref="Changes"/> is a floor rather than a total. A UI that does not say so at these nodes
    /// is presenting a count it has not earned.
    /// </summary>
    public string[] TruncatedPaths { get; }

    /// <summary>
    /// Sides — <c>request</c>, <c>response</c>, <c>event</c> — present on one version and not the
    /// other, so no comparison of that side was possible. A partially-compared topic still carries a
    /// real <see cref="Overall"/> for the sides that <em>were</em> compared; this names what the
    /// verdict does not cover, so a reader is not left to assume it covered everything.
    /// </summary>
    public string[] NotComparedSides { get; }
}
