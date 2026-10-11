using System.Text.Json;
using System.Text.RegularExpressions;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Schema.OpenApi.EventService;
using Xunit;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/contract-hash-cases.json (contract-document.md §6): for every
/// case, the exact <c>contractHash</c> and <c>registrationHash</c>.
/// <list type="bullet">
/// <item>A case without a <c>projection</c> is hashed as it stands by the runtime hash every served
/// document carries (<see cref="ContractHash"/>, in <c>Benzene.Schema.OpenApi</c>) - the producer
/// claim.</item>
/// <item>A case with a <c>projection</c> is projected by the client generator's own code -
/// <see cref="TopicScope.Apply"/> for an include-list (§5.2), <see cref="TopicScope.ProjectTopic"/> for a
/// topic (§5.3) - and the projection's hashes compared; the hash a generated client actually embeds
/// (<c>HashCode</c>) is checked against the expected <c>contractHash</c> too - the client-generator claim.</item>
/// </list>
/// </summary>
public class ContractHashConformanceTest
{
    public class Projection
    {
        public string Kind { get; set; } = string.Empty;
        public string? Topic { get; set; }
        public string[]? Topics { get; set; }
    }

    public class HashCase
    {
        public string Name { get; set; } = string.Empty;
        public JsonElement Document { get; set; }
        public Projection? Projection { get; set; }
        public string ExpectedContractHash { get; set; } = string.Empty;
        public string ExpectedRegistrationHash { get; set; } = string.Empty;

        public override string ToString() => Name;
    }

    public class HashFixture
    {
        public List<HashCase> Cases { get; set; } = new();
    }

    private static readonly Lazy<HashFixture> Fixture = new(() =>
        ConformanceFixtures.Load<HashFixture>("contract-hash-cases.json"));

    public static IEnumerable<object[]> DocumentCases() =>
        Fixture.Value.Cases.Where(c => c.Projection == null).Select(c => new object[] { c });

    public static IEnumerable<object[]> ProjectionCases() =>
        Fixture.Value.Cases.Where(c => c.Projection != null).Select(c => new object[] { c });

    public static IEnumerable<object[]> AllCases() => Fixture.Value.Cases.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(DocumentCases))]
    public void DocumentCase_HashesMatchExactly(HashCase testCase)
    {
        var json = testCase.Document.GetRawText();

        Assert.Equal(testCase.ExpectedContractHash, ContractHash.Compute(json));
        Assert.Equal(testCase.ExpectedRegistrationHash, ContractHash.ComputeRegistrationHash(json));
    }

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void ProjectionCase_TheGeneratorsProjectionHashesMatchExactly(HashCase testCase)
    {
        var projected = Project(testCase);

        Assert.Equal(testCase.ExpectedContractHash, ContractHash.Compute(projected));
        Assert.Equal(testCase.ExpectedRegistrationHash, ContractHash.ComputeRegistrationHash(projected));
        // The served form of the projection carries the recomputed values (§5.2, §5.3).
        Assert.Equal(testCase.ExpectedContractHash, projected.ContractHash);
        Assert.Equal(testCase.ExpectedRegistrationHash, projected.RegistrationHash);
    }

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void ProjectionCase_TheGeneratedClientEmbedsTheProjectionsContractHash(HashCase testCase)
    {
        var document = Deserialize(testCase);
        var projection = testCase.Projection!;

        var files = projection.Kind == "topic"
            ? new AtomicClientSdkBuilder(new ClientSdkOptions { Namespace = "Conformance", Topics = new[] { projection.Topic! } })
                .BuildCodeFiles(document)
            : new MessageClientSdkBuilder(new ClientSdkOptions { ServiceName = "Conformance", Namespace = "Conformance", Topics = projection.Topics })
                .BuildCodeFiles(document);

        var embedded = files
            .SelectMany(file => file.Lines)
            .Select(line => Regex.Match(line, "HashCode => \"(?<hash>[^\"]+)\""))
            .Where(match => match.Success)
            .Select(match => match.Groups["hash"].Value)
            .Distinct()
            .ToArray();

        Assert.Equal(new[] { testCase.ExpectedContractHash }, embedded);
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Hashes_HaveTheSha256Prefix(HashCase testCase)
    {
        foreach (var hash in new[] { testCase.ExpectedContractHash, testCase.ExpectedRegistrationHash })
        {
            Assert.StartsWith("sha256:", hash);
            Assert.Equal(71, hash.Length); // "sha256:" (7) + 64 lowercase hex chars
            Assert.Matches("^[0-9a-f]+$", hash.Substring("sha256:".Length));
        }
    }

    private static EventServiceDocument Deserialize(HashCase testCase)
        => new EventServiceDocumentDeserializer().Deserialize(testCase.Document.GetRawText());

    private static EventServiceDocument Project(HashCase testCase)
    {
        var document = Deserialize(testCase);
        var projection = testCase.Projection!;
        return projection.Kind switch
        {
            "topic" => TopicScope.ProjectTopic(document, document.Requests.Single(r => r.Topic == projection.Topic)),
            "include-list" => TopicScope.Apply(document, new ClientSdkOptions { Namespace = "x", Topics = projection.Topics }),
            var other => throw new InvalidOperationException($"Unknown projection kind '{other}' in the fixture."),
        };
    }
}
