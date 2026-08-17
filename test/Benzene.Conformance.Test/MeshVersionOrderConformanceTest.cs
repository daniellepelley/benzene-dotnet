using System.Text.Json;
using Benzene.Mesh.Contracts;
using Xunit;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/mesh-version-order-cases.json (mesh.md §2.5).
/// </summary>
/// <remarks>
/// <para>
/// A pure-function fixture rather than an envelope one: each case gives two declared service versions
/// of ONE service and the exact outcome a conformant comparison must produce.
/// </para>
/// <para>
/// It exists because "sortable" is not a specification and a comparator is. <c>"10"</c> and
/// <c>"9"</c> order one way as integers and the opposite way as strings, so two ports that each infer
/// a scheme will disagree about which release is newer — inside a tool used to decide deployments.
/// Pinning the three comparators here is what stops that.
/// </para>
/// </remarks>
public class MeshVersionOrderConformanceTest
{
    public class CompareCase
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Set when both sides share one scheme; otherwise the per-side fields carry it.</summary>
        public string? Scheme { get; set; }

        public string? LeftScheme { get; set; }
        public string? RightScheme { get; set; }
        public string Left { get; set; } = string.Empty;
        public string Right { get; set; } = string.Empty;

        /// <summary>-1, 0, 1, or the string "not-orderable".</summary>
        public JsonElement Expected { get; set; }

        public override string ToString() => Name;
    }

    public class ParseCase
    {
        public string Name { get; set; } = string.Empty;
        public string Scheme { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Valid { get; set; }

        public override string ToString() => Name;
    }

    public class OrderFixture
    {
        public List<CompareCase> Compare { get; set; } = new();
        public List<ParseCase> Parse { get; set; } = new();
    }

    private static readonly Lazy<OrderFixture> Fixture = new(() =>
        ConformanceFixtures.Load<OrderFixture>("mesh-version-order-cases.json"));

    public static IEnumerable<object[]> CompareCases() =>
        Fixture.Value.Compare.Select(c => new object[] { c });

    public static IEnumerable<object[]> ParseCases() =>
        Fixture.Value.Parse.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(CompareCases))]
    public void Compare_MatchesTheFixture(CompareCase testCase)
    {
        var leftScheme = testCase.LeftScheme ?? testCase.Scheme;
        var rightScheme = testCase.RightScheme ?? testCase.Scheme;

        var actual = MeshVersionOrder.Compare(leftScheme, testCase.Left, rightScheme, testCase.Right);

        var expected = testCase.Expected.ValueKind == JsonValueKind.String
            ? MeshVersionOrdering.NotOrderable
            : testCase.Expected.GetInt32() switch
            {
                < 0 => MeshVersionOrdering.Earlier,
                0 => MeshVersionOrdering.Same,
                > 0 => MeshVersionOrdering.Later
            };

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(CompareCases))]
    public void Compare_IsAntisymmetric(CompareCase testCase)
    {
        // Not in the fixture, and it should not be: the fixture pins the contract, and this pins that
        // our implementation of it is a coherent order rather than a pile of special cases. Swapping
        // the operands must swap the answer — an asymmetric comparator would sort differently
        // depending on the order it happened to encounter versions in.
        var leftScheme = testCase.LeftScheme ?? testCase.Scheme;
        var rightScheme = testCase.RightScheme ?? testCase.Scheme;

        var forward = MeshVersionOrder.Compare(leftScheme, testCase.Left, rightScheme, testCase.Right);
        var backward = MeshVersionOrder.Compare(rightScheme, testCase.Right, leftScheme, testCase.Left);

        var mirrored = forward switch
        {
            MeshVersionOrdering.Earlier => MeshVersionOrdering.Later,
            MeshVersionOrdering.Later => MeshVersionOrdering.Earlier,
            var same => same
        };

        Assert.Equal(mirrored, backward);
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_MatchesTheFixture(ParseCase testCase)
    {
        if (!MeshVersionOrder.TryParseScheme(testCase.Scheme, out var scheme))
        {
            // An unknown scheme name is itself a rejection — the set is closed, and a port meeting a
            // scheme it does not know must refuse rather than fall back to string comparison.
            Assert.False(testCase.Valid);
            return;
        }

        Assert.Equal(testCase.Valid, MeshVersionOrder.IsValid(scheme, testCase.Value));
    }

    [Fact]
    public void AnInvalidValueIsNeverOrderable()
    {
        // The build that declared it is the cheapest place to catch a mismatch. If one slips through
        // anyway, the comparison must decline rather than produce a confident wrong answer.
        var actual = MeshVersionOrder.Compare("integer", "1.3.0", "integer", "42");

        Assert.Equal(MeshVersionOrdering.NotOrderable, actual);
    }
}
