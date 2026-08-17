using System.Globalization;
using System.Text.RegularExpressions;

namespace Benzene.Mesh.Contracts;

/// <summary>
/// How a declared <c>serviceVersion</c>'s values are compared (mesh.md §2.5).
/// </summary>
/// <remarks>
/// A closed set, declared on the descriptor and never inferred from the value. <c>"10"</c> and
/// <c>"9"</c> order one way as integers and the opposite way as strings, so a port that guesses will
/// disagree with a port that guesses differently — about which release is newer, inside a tool used
/// to decide deployments. There is deliberately no default: a version declared without a scheme is an
/// identity, not a position in an order.
/// </remarks>
public enum MeshVersionScheme
{
    /// <summary>One or more ASCII digits, compared numerically. The build-counter case.</summary>
    Integer,

    /// <summary>A Semantic Versioning 2.0.0 version, compared by SemVer precedence.</summary>
    Semver,

    /// <summary>Any non-empty string, compared codepoint-wise.</summary>
    Lexicographic
}

/// <summary>
/// The outcome of comparing two service versions of one service (mesh.md §2.5).
/// </summary>
public enum MeshVersionOrdering
{
    /// <summary>The left version is earlier in the order.</summary>
    Earlier,

    /// <summary>
    /// The two occupy the same position in the order.
    /// </summary>
    /// <remarks>
    /// Not an assertion that they are the same version. §2.4 is explicit that service-version identity
    /// is extrinsic, so two releases can share a position — two SemVer versions differing only in
    /// build metadata, or a zero-padded build number and its unpadded twin.
    /// </remarks>
    Same,

    /// <summary>The left version is later in the order.</summary>
    Later,

    /// <summary>
    /// The two cannot be placed in one order, and no comparison is offered.
    /// </summary>
    /// <remarks>
    /// A normal outcome, not an error. It arises when the two carry different schemes — a service that
    /// switched from build numbers to SemVer has a real discontinuity in its history — or when either
    /// declares no scheme at all. Inventing an order across such a break would be a claim no data
    /// supports.
    /// </remarks>
    NotOrderable
}

/// <summary>
/// A declared service version and the rule for comparing it (mesh.md §2.4 identity, §2.5 order).
/// </summary>
public readonly record struct MeshServiceVersion(string Value, MeshVersionScheme Scheme);

/// <summary>
/// Ordering for declared service versions — mesh.md §2.5, pinned by
/// <c>conformance/mesh-version-order-cases.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Order is what separates a difference from a direction. Without it, comparing two releases can only
/// report that they differ; with it the same comparison reports an <em>upgrade</em> or a
/// <em>rollback</em>, which is the question anyone planning a deployment is actually asking.
/// </para>
/// <para>
/// Three rules this type exists to keep, all of which are easy to break by being helpful:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>The scheme is declared, never inferred.</b> Nothing here sniffs a value to decide how to
/// compare it.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Order is only defined within one service.</b> There is no global version line, so this type
/// deliberately offers no way to compare across services — the caller holds the service identity.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Order is not lineage.</b> <see cref="MeshVersionOrdering.Later"/> says which version came
/// after, never which contains the other. A hotfix cut from a release branch while trunk moved on
/// orders correctly and is an ancestor of nothing.
/// </description>
/// </item>
/// </list>
/// <para>
/// <c>createdAtUtc</c> is not a substitute and not a tiebreak, which is why no clock appears here.
/// Build timestamps go backwards in practice — rebuilt artifacts, clock skew, pipelines finishing out
/// of order — so a comparator that quietly fell back to one would produce a confident wrong answer
/// exactly when the pipeline was misbehaving.
/// </para>
/// </remarks>
public static class MeshVersionOrder
{
    /// <summary>The wire names of the closed scheme set. Anything else is rejected, never defaulted.</summary>
    private static readonly IReadOnlyDictionary<string, MeshVersionScheme> Schemes =
        new Dictionary<string, MeshVersionScheme>(StringComparer.Ordinal)
        {
            ["integer"] = MeshVersionScheme.Integer,
            ["semver"] = MeshVersionScheme.Semver,
            ["lexicographic"] = MeshVersionScheme.Lexicographic
        };

    /// <summary>One or more ASCII digits. No sign, no separators, no decimal point.</summary>
    private static readonly Regex IntegerPattern = new(@"^[0-9]+$", RegexOptions.Compiled);

    /// <summary>The Semantic Versioning 2.0.0 grammar, verbatim from semver.org.</summary>
    private static readonly Regex SemverPattern = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)"
        + @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)"
        + @"(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
        + @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Resolves a wire scheme name. Returns false for an unknown name — a port meeting a scheme it does
    /// not know must reject it rather than fall back to string comparison, because a silent fallback is
    /// indistinguishable from a correct answer.
    /// </summary>
    public static bool TryParseScheme(string? name, out MeshVersionScheme scheme)
    {
        scheme = default;
        return name != null && Schemes.TryGetValue(name, out scheme);
    }

    /// <summary>The wire name for a scheme.</summary>
    public static string SchemeName(MeshVersionScheme scheme) => scheme switch
    {
        MeshVersionScheme.Integer => "integer",
        MeshVersionScheme.Semver => "semver",
        MeshVersionScheme.Lexicographic => "lexicographic",
        _ => throw new ArgumentOutOfRangeException(nameof(scheme))
    };

    /// <summary>
    /// Whether a value is valid under its declared scheme.
    /// </summary>
    /// <remarks>
    /// Callers are expected to run this where the version is <em>declared</em> — at the build that
    /// emitted it. That is the cheapest place in the system to catch a mismatch; carrying an invalid
    /// version and discovering it at comparison time means a wrong answer has already reached a reader.
    /// </remarks>
    public static bool IsValid(MeshVersionScheme scheme, string? value) => scheme switch
    {
        MeshVersionScheme.Integer => value != null && IntegerPattern.IsMatch(value),
        MeshVersionScheme.Semver => value != null && SemverPattern.IsMatch(value),
        // An empty serviceVersion is §2.4 case 3 — no declared version at all — rather than a version
        // that happens to be blank, so it is not valid under any scheme.
        MeshVersionScheme.Lexicographic => !string.IsNullOrEmpty(value),
        _ => false
    };

    /// <summary>
    /// Compares two service versions <b>of the same service</b>, in their wire form.
    /// </summary>
    /// <remarks>
    /// This is the entry point a caller holding descriptor data actually wants, and it is where §2.5's
    /// "there is no default scheme" rule is enforced: an absent or unrecognised
    /// <c>versionScheme</c> yields <see cref="MeshVersionOrdering.NotOrderable"/> rather than a
    /// fallback comparison. A silent fallback here would be indistinguishable from a correct answer.
    /// </remarks>
    public static MeshVersionOrdering Compare(
        string? leftScheme, string? leftValue, string? rightScheme, string? rightValue)
    {
        if (!TryParseScheme(leftScheme, out var left) || !TryParseScheme(rightScheme, out var right))
        {
            return MeshVersionOrdering.NotOrderable;
        }

        return Compare(
            new MeshServiceVersion(leftValue ?? string.Empty, left),
            new MeshServiceVersion(rightValue ?? string.Empty, right));
    }

    /// <summary>
    /// Compares two service versions <b>of the same service</b>.
    /// </summary>
    /// <returns>
    /// Where <paramref name="left"/> sits relative to <paramref name="right"/>, or
    /// <see cref="MeshVersionOrdering.NotOrderable"/> when the two carry different schemes or either is
    /// invalid under its own.
    /// </returns>
    public static MeshVersionOrdering Compare(MeshServiceVersion left, MeshServiceVersion right)
    {
        if (left.Scheme != right.Scheme)
        {
            // Different schemes are not orderable even when both values would parse under either one.
            // Agreeing by accident on a single pair of values is not an order.
            return MeshVersionOrdering.NotOrderable;
        }

        if (!IsValid(left.Scheme, left.Value) || !IsValid(right.Scheme, right.Value))
        {
            return MeshVersionOrdering.NotOrderable;
        }

        var sign = left.Scheme switch
        {
            MeshVersionScheme.Integer => CompareIntegers(left.Value, right.Value),
            MeshVersionScheme.Semver => CompareSemver(left.Value, right.Value),
            MeshVersionScheme.Lexicographic => Math.Sign(string.CompareOrdinal(left.Value, right.Value)),
            _ => 0
        };

        return sign < 0 ? MeshVersionOrdering.Earlier
            : sign > 0 ? MeshVersionOrdering.Later
            : MeshVersionOrdering.Same;
    }

    /// <summary>
    /// Compares two all-digit strings by numeric value, at arbitrary precision.
    /// </summary>
    /// <remarks>
    /// Done on the digits rather than through a fixed-width integer on purpose. Build counters do not
    /// outrun 64 bits in any real pipeline, but a comparator that silently overflows is worse than one
    /// that refuses, and this way the behaviour does not depend on the port's integer width.
    /// </remarks>
    private static int CompareIntegers(string left, string right)
    {
        var l = left.TrimStart('0');
        var r = right.TrimStart('0');
        if (l.Length != r.Length)
        {
            return l.Length < r.Length ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(l, r));
    }

    /// <summary>SemVer 2.0.0 §11 precedence. Build metadata is ignored (§10).</summary>
    private static int CompareSemver(string left, string right)
    {
        var l = SemverPattern.Match(left);
        var r = SemverPattern.Match(right);

        foreach (var part in new[] { "major", "minor", "patch" })
        {
            var comparison = CompareIntegers(l.Groups[part].Value, r.Groups[part].Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        var leftPre = l.Groups["prerelease"];
        var rightPre = r.Groups["prerelease"];
        if (!leftPre.Success && !rightPre.Success)
        {
            return 0;
        }

        // A pre-release has lower precedence than the normal version it precedes (§11.3).
        if (!leftPre.Success)
        {
            return 1;
        }

        if (!rightPre.Success)
        {
            return -1;
        }

        return ComparePrerelease(leftPre.Value.Split('.'), rightPre.Value.Split('.'));
    }

    /// <summary>SemVer 2.0.0 §11.4: identifiers left to right, numeric below alphanumeric.</summary>
    private static int ComparePrerelease(string[] left, string[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = IntegerPattern.IsMatch(left[i]);
            var rightNumeric = IntegerPattern.IsMatch(right[i]);

            int comparison;
            if (leftNumeric && rightNumeric)
            {
                // Numerically, so rc.10 is later than rc.9 — the same digits-versus-string trap the
                // whole of §2.5 exists for, one level down.
                comparison = CompareIntegers(left[i], right[i]);
            }
            else if (leftNumeric != rightNumeric)
            {
                // Numeric identifiers always have lower precedence than alphanumeric ones.
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = Math.Sign(string.CompareOrdinal(left[i], right[i]));
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        // All the identifiers they share are equal, so the longer set wins (§11.4.4).
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>
    /// Whether a version order and a build-time order <b>disagree</b> — a later version built earlier.
    /// </summary>
    /// <remarks>
    /// Surfacing this is the point (mesh.md §2.5). It means an out-of-order pipeline, a rebuilt artifact
    /// or a backdated tag, each of which is worth knowing, and none of which should be quietly
    /// reconciled by preferring one field over the other.
    /// </remarks>
    public static bool DisagreesWithBuildTime(
        MeshServiceVersion left, DateTimeOffset leftCreatedAt,
        MeshServiceVersion right, DateTimeOffset rightCreatedAt)
    {
        var order = Compare(left, right);
        return order switch
        {
            MeshVersionOrdering.Later => leftCreatedAt < rightCreatedAt,
            MeshVersionOrdering.Earlier => leftCreatedAt > rightCreatedAt,
            _ => false
        };
    }

    /// <summary>
    /// The latest of a service's versions, or null when they cannot all be placed in one order.
    /// </summary>
    /// <remarks>
    /// Null rather than a best guess: "the newest version of this service" is the basis of a tip
    /// composition and of every "N versions behind" statement, and answering it from a set that
    /// contains a scheme discontinuity would put an unfounded number in front of a reader.
    /// </remarks>
    public static MeshServiceVersion? Latest(IEnumerable<MeshServiceVersion> versions)
    {
        MeshServiceVersion? latest = null;
        foreach (var version in versions)
        {
            if (latest == null)
            {
                if (!IsValid(version.Scheme, version.Value))
                {
                    return null;
                }

                latest = version;
                continue;
            }

            var order = Compare(version, latest.Value);
            if (order == MeshVersionOrdering.NotOrderable)
            {
                return null;
            }

            if (order == MeshVersionOrdering.Later)
            {
                latest = version;
            }
        }

        return latest;
    }

    /// <summary>
    /// How many versions separate <paramref name="from"/> and <paramref name="to"/> in
    /// <paramref name="known"/> — the "four versions behind" figure.
    /// </summary>
    /// <returns>
    /// A non-negative count, or null when the set cannot be ordered or either endpoint is not in it.
    /// Null is a real answer and must not be rendered as zero.
    /// </returns>
    public static int? Distance(
        MeshServiceVersion from, MeshServiceVersion to, IEnumerable<MeshServiceVersion> known)
    {
        var ordered = new List<MeshServiceVersion>();
        foreach (var version in known)
        {
            if (!IsValid(version.Scheme, version.Value))
            {
                return null;
            }

            if (ordered.Count > 0 && Compare(version, ordered[0]) == MeshVersionOrdering.NotOrderable)
            {
                return null;
            }

            ordered.Add(version);
        }

        ordered.Sort((a, b) => Compare(a, b) switch
        {
            MeshVersionOrdering.Earlier => -1,
            MeshVersionOrdering.Later => 1,
            _ => 0
        });

        var fromIndex = ordered.FindIndex(v => Compare(v, from) == MeshVersionOrdering.Same);
        var toIndex = ordered.FindIndex(v => Compare(v, to) == MeshVersionOrdering.Same);
        if (fromIndex < 0 || toIndex < 0)
        {
            return null;
        }

        return Math.Abs(toIndex - fromIndex);
    }

    /// <summary>Formats a version for display, scheme included, so a reader can see which order applies.</summary>
    public static string Describe(MeshServiceVersion version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Value} ({SchemeName(version.Scheme)})");
}
