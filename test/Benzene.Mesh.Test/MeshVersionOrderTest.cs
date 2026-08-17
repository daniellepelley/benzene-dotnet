using Benzene.Mesh.Contracts;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// The derived helpers over mesh.md §2.5's ordering — the ones a reader's questions actually reduce
/// to. The comparator itself is pinned by the language-neutral fixture
/// (<c>mesh-version-order-cases.json</c>) and is not re-tested here.
/// </summary>
/// <remarks>
/// Every one of these has a null or NOT ORDERABLE arm, and those arms are the point. "Newest version"
/// and "four versions behind" are figures that go straight in front of a reader deciding a
/// deployment; answering either from a set that cannot be ordered would put an unfounded number on
/// screen, which is the defect class the whole product has spent seven rounds removing.
/// </remarks>
public class MeshVersionOrderTest
{
    private static MeshServiceVersion Build(string value) => new(value, MeshVersionScheme.Integer);

    private static MeshServiceVersion Semver(string value) => new(value, MeshVersionScheme.Semver);

    [Fact]
    public void Latest_PicksTheNewestBuild()
    {
        var latest = MeshVersionOrder.Latest([Build("9"), Build("41"), Build("10")]);

        // Not "41" by string comparison, which would pick "9".
        Assert.Equal(Build("41"), latest);
    }

    [Fact]
    public void Latest_IsNullAcrossASchemeChange()
    {
        // A service that switched from build numbers to SemVer has a real discontinuity. There is no
        // newest version across it, and a tip composition built on a guess would be worse than none.
        var latest = MeshVersionOrder.Latest([Build("41"), Semver("1.3.0")]);

        Assert.Null(latest);
    }

    [Fact]
    public void Latest_IsNullWhenAValueDoesNotParseUnderItsScheme()
    {
        Assert.Null(MeshVersionOrder.Latest([Build("41"), Build("1.3.0")]));
    }

    [Fact]
    public void Latest_IsNullForAnEmptySet()
    {
        // No versions is not version zero.
        Assert.Null(MeshVersionOrder.Latest([]));
    }

    [Fact]
    public void Distance_CountsTheVersionsBetween()
    {
        var known = new[] { Build("40"), Build("41"), Build("42"), Build("43") };

        Assert.Equal(3, MeshVersionOrder.Distance(Build("40"), Build("43"), known));
    }

    [Fact]
    public void Distance_CountsBySTEPSNotByArithmetic()
    {
        // "Four versions behind" means four releases, not a subtraction of build numbers. A pipeline
        // that skips numbers — a failed build, a retagged artifact — would otherwise inflate the gap.
        var known = new[] { Build("10"), Build("20"), Build("30") };

        Assert.Equal(2, MeshVersionOrder.Distance(Build("10"), Build("30"), known));
    }

    [Fact]
    public void Distance_IsZeroForTheSameVersion()
    {
        var known = new[] { Build("41"), Build("42") };

        Assert.Equal(0, MeshVersionOrder.Distance(Build("42"), Build("42"), known));
    }

    [Fact]
    public void Distance_IsNullWhenAnEndpointIsNotInTheKnownSet()
    {
        // The catalogue does not contain that build, so the count is unknown — never zero, which would
        // read as "up to date".
        var known = new[] { Build("40"), Build("41") };

        Assert.Null(MeshVersionOrder.Distance(Build("40"), Build("99"), known));
    }

    [Fact]
    public void Distance_IsNullAcrossASchemeChange()
    {
        var known = new[] { Build("41"), Semver("1.3.0") };

        Assert.Null(MeshVersionOrder.Distance(Build("41"), Semver("1.3.0"), known));
    }

    [Fact]
    public void DisagreesWithBuildTime_IsFalseWhenTheOrdersAgree()
    {
        var earlier = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.False(MeshVersionOrder.DisagreesWithBuildTime(Build("42"), later, Build("41"), earlier));
    }

    [Fact]
    public void DisagreesWithBuildTime_IsTrueWhenALaterVersionWasBuiltEarlier()
    {
        // The finding mesh.md §2.5 asks to be surfaced: an out-of-order pipeline, a rebuilt artifact,
        // or a backdated tag. Worth telling somebody about rather than reconciling silently by
        // preferring one field over the other.
        var earlier = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.True(MeshVersionOrder.DisagreesWithBuildTime(Build("42"), earlier, Build("41"), later));
    }

    [Fact]
    public void DisagreesWithBuildTime_IsFalseWhenTheTwoAreNotOrderable()
    {
        // Nothing to disagree with. Reporting a disagreement here would be inventing an order to
        // contradict.
        var earlier = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.False(MeshVersionOrder.DisagreesWithBuildTime(Build("42"), earlier, Semver("1.3.0"), later));
    }

    [Fact]
    public void Describe_NamesTheSchemeSoAReaderKnowsWhichOrderApplies()
    {
        Assert.Equal("42 (integer)", MeshVersionOrder.Describe(Build("42")));
    }

    [Fact]
    public void SchemeNamesRoundTripThroughTheWire()
    {
        foreach (var scheme in Enum.GetValues<MeshVersionScheme>())
        {
            Assert.True(MeshVersionOrder.TryParseScheme(MeshVersionOrder.SchemeName(scheme), out var parsed));
            Assert.Equal(scheme, parsed);
        }
    }

    [Fact]
    public void AnUnknownSchemeNameIsRejectedRatherThanDefaulted()
    {
        // §2.5: the set is closed and there is no default. A silent fallback to string comparison is
        // indistinguishable from a correct answer, which is what makes it dangerous.
        Assert.False(MeshVersionOrder.TryParseScheme("calver", out _));
        Assert.False(MeshVersionOrder.TryParseScheme(null, out _));
    }
}
