using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Mesh.Wire;

/// <summary>
/// An issue's error detail (spec §4.1, candidate 1.2) on the emitter side and in decoding: the
/// <c>UseMeshIssues</c> middleware takes the failing result's (field, code) pairs and never its message;
/// <see cref="HttpMeshIssueExporter"/> counts them per window as deltas, bounds them to ten at the flush
/// in the normative order, and caps the distinct pairs it holds; and the wire's <c>errors</c> decodes
/// leniently, so a malformed entry never costs the issue or its batch. The collector's merge and bound
/// are pinned by <c>mesh-issue-cases.json</c>.
/// </summary>
public class MeshIssueErrorsTest
{
    private const string Url = "http://mesh.internal/mesh/envelope";
    private const string SecretMessage = "order 4111-1111 for jane@example.com is invalid";

    [Message("orders:validate")]
    public class ValidationHandler : IMessageHandler<Void, Void>
    {
        public Task<IBenzeneResult<Void>> HandleAsync(Void request)
            => Task.FromResult(BenzeneResult.Set<Void>(BenzeneResultStatus.ValidationError, new[]
            {
                new BenzeneError(SecretMessage, "/orderId", "required"),
                new BenzeneError(SecretMessage, "/quantity", "GreaterThanValidator"),
                new BenzeneError(SecretMessage, "/orderId", "required"),
                new BenzeneError(SecretMessage, null, "TotalsDisagree"),
                new BenzeneError(SecretMessage) // neither field nor code: not counted
            }));
    }

    [Message("orders:messages-only")]
    public class MessagesOnlyHandler : IMessageHandler<Void, Void>
    {
        public Task<IBenzeneResult<Void>> HandleAsync(Void request)
            => Task.FromResult(BenzeneResult.SetFailed<Void>(BenzeneResultStatus.ValidationError, SecretMessage));
    }

    private sealed class CapturingIssueExporter : IMeshIssueExporter
    {
        public readonly List<MeshIssueOccurrence> Occurrences = new();
        public void Export(MeshIssueOccurrence occurrence) => Occurrences.Add(occurrence);
    }

    /// <summary>Records every POSTed envelope body — the wire the exporter actually sends.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<string> _posts = new();

        public IReadOnlyList<string> Posts
        {
            get { lock (_posts) { return _posts.ToList(); } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (_posts)
            {
                _posts.Add(body);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static async Task RunAsync(IMeshIssueExporter exporter, string topic)
    {
        var handlers = new[] { typeof(ValidationHandler), typeof(MessagesOnlyHandler) };
        var services = new ServiceCollection();
        services.AddLogging();
        services.UsingBenzene(x => x.AddBenzene().AddBenzeneMessage().AddMessageHandlers(handlers));

        var info = new MeshServiceInfo("orders");
        var container = new MicrosoftBenzeneServiceContainer(services);
        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipeline
            .UseMeshIssues(info, exporter, new BenzeneMessageMeshStatusReader())
            .UseMessageHandlers(handlers);

        await new BenzeneMessageApplication(pipeline.Build()).HandleAsync(new BenzeneMessageRequest
        {
            Topic = topic, Headers = new Dictionary<string, string>(), Body = "{}"
        }, new MicrosoftServiceResolverFactory(services));
    }

    /// <summary>The issues of every POSTed batch that carried any, in send order.</summary>
    private static List<MeshIssue> IssuesPosted(CapturingHandler handler) =>
        handler.Posts.SelectMany(post =>
        {
            using var envelope = JsonDocument.Parse(post);
            var body = envelope.RootElement.GetProperty("body").GetString()!;
            return JsonSerializer.Deserialize<MeshIssueBatch>(body, MeshJson.Options)!.Issues;
        }).ToList();

    private static HttpMeshIssueExporter Exporter(CapturingHandler handler, TimeSpan? flushInterval = null) =>
        new(new HttpClient(handler), Url, "orders", flushInterval ?? TimeSpan.FromHours(1));

    private static MeshIssueOccurrence Occurrence(params MeshIssueError[] errors) => new()
    {
        Service = "orders",
        Topic = "order:create",
        Status = BenzeneResultStatus.ValidationError,
        At = DateTimeOffset.UtcNow,
        Errors = errors
    };

    private static MeshIssueError Pair(string? field, string? code, long count = 1) =>
        new() { Field = field, Code = code, Count = count };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task Middleware_TakesTheFailingResultsFieldAndCodePairs_NeverTheMessage()
    {
        var exporter = new CapturingIssueExporter();

        await RunAsync(exporter, "orders:validate");

        var occurrence = Assert.Single(exporter.Occurrences);
        // One entry per counted error, in result order; the error with neither member is dropped.
        Assert.Equal(
            new (string?, string?, long)[]
            {
                ("/orderId", "required", 1), ("/quantity", "GreaterThanValidator", 1),
                ("/orderId", "required", 1), (null, "TotalsDisagree", 1)
            },
            occurrence.Errors!.Select(e => (e.Field, e.Code, e.Count)).ToArray());
    }

    [Fact]
    public async Task Middleware_ResultWithOnlyMessages_CarriesNoErrorDetail()
    {
        var exporter = new CapturingIssueExporter();

        await RunAsync(exporter, "orders:messages-only");

        Assert.Null(Assert.Single(exporter.Occurrences).Errors);
    }

    [Fact]
    public async Task EndToEnd_TheWireCarriesCountedPairs_AndNoMessageText()
    {
        var handler = new CapturingHandler();
        var exporter = Exporter(handler);

        await RunAsync(exporter, "orders:validate");
        await RunAsync(exporter, "orders:validate");
        await exporter.DisposeAsync(); // tail flush

        var issue = Assert.Single(IssuesPosted(handler));
        Assert.Equal(2, issue.Count);
        // Per pair, summed over both failures; ordered count descending, then field (absent first).
        Assert.Equal(
            new (string?, string?, long)[] { ("/orderId", "required", 4), (null, "TotalsDisagree", 2), ("/quantity", "GreaterThanValidator", 2) },
            issue.Errors!.Select(e => (e.Field, e.Code, e.Count)).ToArray());
        // The message is free text built from the payload: it never reaches the wire.
        Assert.DoesNotContain(handler.Posts, post => post.Contains("4111") || post.Contains("jane@example.com"));
        Assert.DoesNotContain(handler.Posts, post => post.Contains("message", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Flush_BoundsToTen_ByCountThenFieldThenCode_AbsentFirst()
    {
        var handler = new CapturingHandler();
        var exporter = Exporter(handler);

        exporter.Export(Occurrence(Pair("/k", "required", 5)));
        exporter.Export(Occurrence(Pair(null, "z")));
        exporter.Export(Occurrence(Pair("/b", "required"), Pair("/b", null), Pair("/a", "required")));
        foreach (var field in new[] { "/j", "/i", "/h", "/g", "/f", "/e", "/d", "/c" })
        {
            exporter.Export(Occurrence(Pair(field, "required")));
        }
        await exporter.DisposeAsync();

        var issue = Assert.Single(IssuesPosted(handler));
        Assert.Equal(11, issue.Count);
        // 13 distinct pairs held in the window; the wire carries the ten highest, in the spec order:
        // count desc, then field, then code — ordinal, absent first (so ("/b", null) precedes ("/b", "required")).
        Assert.Equal(
            new (string?, string?, long)[]
            {
                ("/k", "required", 5), (null, "z", 1), ("/a", "required", 1), ("/b", null, 1), ("/b", "required", 1),
                ("/c", "required", 1), ("/d", "required", 1), ("/e", "required", 1), ("/f", "required", 1), ("/g", "required", 1)
            },
            issue.Errors!.Select(e => (e.Field, e.Code, e.Count)).ToArray());
    }

    [Fact]
    public async Task Flush_NoErrorDetail_OmitsErrorsFromTheWire()
    {
        var handler = new CapturingHandler();
        var exporter = Exporter(handler);

        exporter.Export(Occurrence());
        await exporter.DisposeAsync();

        Assert.Null(Assert.Single(IssuesPosted(handler)).Errors);
        Assert.DoesNotContain(handler.Posts, post => post.Contains("errors"));
    }

    [Fact]
    public async Task Window_CapsDistinctPairs_ButKeepsCountingAdmittedOnes()
    {
        var handler = new CapturingHandler();
        var exporter = Exporter(handler);

        // Fill the window to the distinct-pair cap...
        exporter.Export(Occurrence(Enumerable.Range(0, MeshIssueErrors.MaxPendingPairs)
            .Select(i => Pair($"/p{i:000}", "required")).ToArray()));
        // ...then a new pair, however frequent, is not admitted (bounded memory)...
        for (var i = 0; i < 20; i++)
        {
            exporter.Export(Occurrence(Pair("/late", "required")));
        }
        // ...while a pair already held keeps counting.
        for (var i = 0; i < 5; i++)
        {
            exporter.Export(Occurrence(Pair("/p050", "required")));
        }
        await exporter.DisposeAsync();

        var issue = Assert.Single(IssuesPosted(handler));
        Assert.Equal(26, issue.Count); // the issue's own count is unaffected by the cap
        Assert.Equal(MeshIssueErrors.MaxEntries, issue.Errors!.Count);
        Assert.DoesNotContain(issue.Errors, e => e.Field == "/late");
        Assert.Equal(("/p050", 6L), (issue.Errors[0].Field, issue.Errors[0].Count));
        Assert.Equal("/p000", issue.Errors[1].Field);
    }

    [Fact]
    public async Task Window_CountsAreDeltas_EachFlushStartsAfresh()
    {
        var handler = new CapturingHandler();
        var exporter = Exporter(handler, TimeSpan.FromMilliseconds(100));

        exporter.Export(Occurrence(Pair("/orderId", "required")));
        Assert.True(await WaitUntilAsync(() => IssuesPosted(handler).Count == 1, TimeSpan.FromSeconds(5)));
        exporter.Export(Occurrence(Pair("/orderId", "required")));
        await exporter.DisposeAsync();

        var issues = IssuesPosted(handler);
        Assert.Equal(2, issues.Count);
        // Never cumulative: the second window reports only its own occurrence.
        Assert.All(issues, issue => Assert.Equal(("/orderId", "required", 1L),
            (Assert.Single(issue.Errors!).Field, issue.Errors![0].Code, issue.Errors[0].Count)));
    }

    private const string MalformedErrors = """
        {"service":"orders","issues":[{"fingerprint":"cccc3333cccc3333cccc3333cccc3333","classification":"validation",
          "service":"orders","topic":"order:create","status":"validation-error","count":2,
          "firstSeen":"2026-07-25T09:00:00Z","lastSeen":"2026-07-25T09:00:00Z",
          "errors":["required",42,null,[],{"count":1},{"field":"/a","count":0},{"field":"/a","count":-1},
            {"field":"/a","count":1.5},{"field":"/a","count":2.0},{"field":"/a","count":"2"},{"field":"/a","code":"required"},
            {"field":5,"count":1},{"field":"","code":"","count":1},{"field":"/b","code":"required","count":2}]}]}
        """;

    public static IEnumerable<object[]> Deserializers() => new[]
    {
        new object[] { "MeshJson", (Func<string, MeshIssueBatch>)(json => JsonSerializer.Deserialize<MeshIssueBatch>(json, MeshJson.Options)!) },
        // The collector's own request path: Benzene's default (case-insensitive, camelCase) serializer.
        new object[] { "Benzene", (Func<string, MeshIssueBatch>)(json => new Benzene.Core.MessageHandlers.Serialization.JsonSerializer().Deserialize<MeshIssueBatch>(json)) }
    };

    [Theory]
    [MemberData(nameof(Deserializers))]
    public void Decode_SkipsMalformedEntries_AndKeepsTheIssue(string name, Func<string, MeshIssueBatch> deserialize)
    {
        var batch = deserialize(MalformedErrors);

        var issue = Assert.Single(batch.Issues);
        Assert.Equal(2, issue.Count);
        // Not an object, neither member, a count that is not a positive integer (0, -1, 1.5, 2.0, "2"),
        // a non-string member, or empty members: each skipped. Only the well-formed entry survives.
        Assert.True(issue.Errors is { Count: 1 }, $"{name}: expected exactly the one well-formed entry");
        Assert.Equal(("/b", "required", 2L), (issue.Errors![0].Field, issue.Errors[0].Code, issue.Errors[0].Count));
    }

    [Theory]
    [InlineData("\"errors\":\"required\"")]
    [InlineData("\"errors\":{\"field\":\"/a\",\"count\":1}")]
    [InlineData("\"errors\":7")]
    [InlineData("\"errors\":null")]
    public void Decode_ErrorsThatIsNotAnArray_ReadsAsNoDetail(string member)
    {
        var json = "{\"service\":\"orders\",\"issues\":[{\"fingerprint\":\"f\",\"topic\":\"t\",\"count\":1," + member + ",\"status\":\"x\"}]}";

        var issue = Assert.Single(JsonSerializer.Deserialize<MeshIssueBatch>(json, MeshJson.Options)!.Issues);

        Assert.Null(issue.Errors);
        Assert.Equal("x", issue.Status); // the reader resumed correctly after the skipped value
    }

    [Fact]
    public void Collector_AcceptsTheBatch_AndMergesOnlyTheWellFormedEntry()
    {
        var store = new MeshCollectorStore();

        var accepted = store.AddIssues(JsonSerializer.Deserialize<MeshIssueBatch>(MalformedErrors, MeshJson.Options)!);

        Assert.Equal(1, accepted);
        var entry = Assert.Single(Assert.Single(store.Fleet().Issues).Errors!);
        Assert.Equal(("/b", "required", 2L), (entry.Field, entry.Code, entry.Count));
    }

    [Fact]
    public void Collector_InProcessMalformedEntries_AreSkippedByTheMerge()
    {
        // A caller handing AddIssues objects directly bypasses the lenient decoder; the merge still
        // drops what the wire decoder would have.
        var store = new MeshCollectorStore();
        var batch = new MeshIssueBatch
        {
            Service = "orders",
            Issues = new List<MeshIssue>
            {
                new()
                {
                    Fingerprint = "f", Topic = "t", Count = 1,
                    Errors = new List<MeshIssueError> { null!, Pair(null, null, 3), Pair("/a", "x", 0), Pair("", "", 4), Pair("/b", "", 2) }
                }
            }
        };

        store.AddIssues(batch);

        var entry = Assert.Single(Assert.Single(store.Fleet().Issues).Errors!);
        Assert.Equal(("/b", (string?)null, 2L), (entry.Field, entry.Code, entry.Count)); // "" reads as absent
    }

    [Fact]
    public async Task Fingerprint_IgnoresErrorDetail()
    {
        // Two failures differing only in their error detail are one issue (spec §4.1).
        var handler = new CapturingHandler();
        var exporter = Exporter(handler);

        exporter.Export(Occurrence(Pair("/a", "required")));
        exporter.Export(Occurrence(Pair("/b", "other")));
        await exporter.DisposeAsync();

        var issue = Assert.Single(IssuesPosted(handler));
        Assert.Equal(2, issue.Count);
        Assert.Equal(2, issue.Errors!.Count);
    }
}
