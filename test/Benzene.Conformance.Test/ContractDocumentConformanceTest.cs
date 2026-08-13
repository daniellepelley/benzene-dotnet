using System.Text.Json;
using Benzene.CodeGen.Client;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Xunit;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/contract-document-cases.json: parse/validate cases (§1-§2 of
/// contract-document.md, including the §5.1 reserved-detection rule and the §5.2 fail-loud
/// unknown-topic rule), topic-scope projection cases (§5.2, run through the actual
/// <see cref="TopicScope"/> implementation - internal, exposed to this project via
/// <c>InternalsVisibleTo</c> on <c>Benzene.CodeGen.Client</c>), and schema-closure cases (§5.3, run
/// through <see cref="SchemaClosure"/>, the same walk <c>AtomicClientSdkBuilder</c> uses).
/// </summary>
public class ContractDocumentConformanceTest
{
    public class Fixture
    {
        public Dictionary<string, JsonElement> Documents { get; set; } = new();
        public List<ParseCase> ParseCases { get; set; } = new();
        public List<TopicScopeCase> TopicScopeCases { get; set; } = new();
        public List<SchemaClosureCase> SchemaClosureCases { get; set; } = new();
    }

    public class TopicOptions
    {
        public string[]? Topics { get; set; }
        public bool IncludeReserved { get; set; }
    }

    public class ExpectedEntry
    {
        public string Topic { get; set; } = string.Empty;
        public bool VersionPresent { get; set; }
        public string? Version { get; set; }
        public bool? Reserved { get; set; }
    }

    public class ExpectedShape
    {
        public List<ExpectedEntry>? Requests { get; set; }
        public List<ExpectedEntry>? Events { get; set; }
    }

    public class ExpectedErrorShape
    {
        public string[] UnknownTopics { get; set; } = Array.Empty<string>();
        public string[] ValidTopics { get; set; } = Array.Empty<string>();
    }

    public class ParseCase
    {
        public string Name { get; set; } = string.Empty;
        public string DocumentRef { get; set; } = string.Empty;
        public TopicOptions? Options { get; set; }
        public ExpectedShape? Expected { get; set; }
        public ExpectedErrorShape? ExpectedError { get; set; }

        public override string ToString() => Name;
    }

    public class TopicScopeCase
    {
        public string Name { get; set; } = string.Empty;
        public string DocumentRef { get; set; } = string.Empty;
        public TopicOptions Options { get; set; } = new();
        public string[] ExpectedTopics { get; set; } = Array.Empty<string>();

        public override string ToString() => Name;
    }

    public class SchemaClosureCase
    {
        public string Name { get; set; } = string.Empty;
        public string DocumentRef { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public string[] ExpectedComponents { get; set; } = Array.Empty<string>();

        public override string ToString() => Name;
    }

    private static readonly Lazy<Fixture> FixtureData = new(() =>
        ConformanceFixtures.Load<Fixture>("contract-document-cases.json"));

    private static EventServiceDocument DeserializeDocument(string documentRef)
    {
        var json = FixtureData.Value.Documents[documentRef].GetRawText();
        return new EventServiceDocumentDeserializer().Deserialize(json);
    }

    public static IEnumerable<object[]> ParseCases() => FixtureData.Value.ParseCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> TopicScopeCases() => FixtureData.Value.TopicScopeCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> SchemaClosureCases() => FixtureData.Value.SchemaClosureCases.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void ParseCase_MatchesTheExpectedShapeOrFailsLoud(ParseCase testCase)
    {
        var document = DeserializeDocument(testCase.DocumentRef);

        if (testCase.ExpectedError != null)
        {
            var options = new ClientSdkOptions { Namespace = "x", Topics = testCase.Options?.Topics };

            var exception = Assert.Throws<ArgumentException>(() => TopicScope.Apply(document, options));

            foreach (var unknownTopic in testCase.ExpectedError.UnknownTopics)
            {
                Assert.Contains(unknownTopic, exception.Message);
            }

            foreach (var validTopic in testCase.ExpectedError.ValidTopics)
            {
                Assert.Contains(validTopic, exception.Message);
            }

            return;
        }

        if (testCase.Expected?.Requests != null)
        {
            foreach (var expectedRequest in testCase.Expected.Requests)
            {
                var actual = document.Requests.Single(r => r.Topic == expectedRequest.Topic);
                AssertVersion(expectedRequest, actual.Version);

                if (expectedRequest.Reserved.HasValue)
                {
                    // §5.1's full detection rule: the raw `reserved` flag OR the `benzene:` prefix.
                    var detectedReserved = actual.Reserved || ReservedTopics.IsReserved(actual.Topic);
                    Assert.Equal(expectedRequest.Reserved.Value, detectedReserved);
                }
            }
        }

        if (testCase.Expected?.Events != null)
        {
            foreach (var expectedEvent in testCase.Expected.Events)
            {
                var actual = document.Events.Single(e => e.Topic == expectedEvent.Topic);
                AssertVersion(expectedEvent, actual.Version);
            }
        }
    }

    private static void AssertVersion(ExpectedEntry expected, string actualVersion)
    {
        if (!expected.VersionPresent)
        {
            Assert.True(string.IsNullOrEmpty(actualVersion));
            return;
        }

        Assert.False(string.IsNullOrEmpty(actualVersion));
        if (expected.Version != null)
        {
            Assert.Equal(expected.Version, actualVersion);
        }
    }

    [Theory]
    [MemberData(nameof(TopicScopeCases))]
    public void TopicScopeCase_ProjectsToExactlyTheExpectedTopics(TopicScopeCase testCase)
    {
        var document = DeserializeDocument(testCase.DocumentRef);
        var options = new ClientSdkOptions
        {
            Namespace = "x",
            Topics = testCase.Options.Topics,
            IncludeReservedTopics = testCase.Options.IncludeReserved,
        };

        var scoped = TopicScope.Apply(document, options);

        // Set-compared, order-independent, no duplicates - per the fixture's own comparison rule
        // (see the fixture's top-level "description"). xUnit's Assert.Equal on two HashSet<string>
        // instances compares by enumeration order, not set membership, so both sides are sorted first.
        var actualTopics = scoped.Requests.Select(r => r.Topic).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var expectedTopics = testCase.ExpectedTopics.OrderBy(t => t, StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedTopics.Length, scoped.Requests.Length); // no duplicates
        Assert.Equal(expectedTopics, actualTopics);
    }

    [Theory]
    [MemberData(nameof(SchemaClosureCases))]
    public void SchemaClosureCase_ReachesExactlyTheExpectedComponents(SchemaClosureCase testCase)
    {
        var document = DeserializeDocument(testCase.DocumentRef);
        var request = document.Requests.Single(r => r.Topic == testCase.Topic);

        var reached = SchemaClosure.ReachableNames(document.Components.Schemas, request.Request, request.Response);

        var actual = reached.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var expected = testCase.ExpectedComponents.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }
}
