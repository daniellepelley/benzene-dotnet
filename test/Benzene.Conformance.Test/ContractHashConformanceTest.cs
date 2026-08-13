using System.Text.Json;
using Benzene.CodeGen.Core;
using Benzene.Schema.OpenApi.EventService;
using Xunit;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/contract-hash-cases.json: for each case's already-projected
/// document, the spec-pinned <c>contractHash</c> algorithm (contract-document.md §6) must reproduce
/// the fixture's exact <c>expectedHash</c>. None of these four cases carries a reserved entry
/// through a topic-scoped (§5.3) projection - the fixture's own description says so - so every case
/// is computed as a whole-service/default (<c>isTopicScoped: false</c>) hash; that flag only changes
/// behaviour when a reserved entry actually survives scoping, which no case here exercises.
/// </summary>
public class ContractHashConformanceTest
{
    public class HashCase
    {
        public string Name { get; set; } = string.Empty;
        public JsonElement Document { get; set; }
        public string ExpectedHash { get; set; } = string.Empty;

        public override string ToString() => Name;
    }

    public class HashFixture
    {
        public List<HashCase> Cases { get; set; } = new();
    }

    private static readonly Lazy<HashFixture> Fixture = new(() =>
        ConformanceFixtures.Load<HashFixture>("contract-hash-cases.json"));

    public static IEnumerable<object[]> Cases() => Fixture.Value.Cases.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ContractHash_MatchesTheFixturesExpectedHash(HashCase testCase)
    {
        var document = new EventServiceDocumentDeserializer().Deserialize(testCase.Document.GetRawText());

        var actual = ContractHash.Compute(document);

        Assert.Equal(testCase.ExpectedHash, actual);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ContractHash_HasTheSha256Prefix(HashCase testCase)
    {
        Assert.StartsWith("sha256:", testCase.ExpectedHash);
        Assert.Equal(71, testCase.ExpectedHash.Length); // "sha256:" (7) + 64 lowercase hex chars
        Assert.Matches("^[0-9a-f]+$", testCase.ExpectedHash.Substring("sha256:".Length));
    }
}
