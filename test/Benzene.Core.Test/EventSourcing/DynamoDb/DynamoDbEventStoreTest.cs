using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.EventSourcing;
using Benzene.EventSourcing.DynamoDb;
using Moq;
using Xunit;

namespace Benzene.Test.EventSourcing.DynamoDb;

public class DynamoDbEventStoreTest
{
    private static Dictionary<string, AttributeValue> EventItem(string streamId, long version, string type)
        => new()
        {
            ["pk"] = new AttributeValue { S = streamId },
            ["version"] = new AttributeValue { N = version.ToString() },
            ["eventType"] = new AttributeValue { S = type },
            ["payload"] = new AttributeValue { S = "{}" },
            ["timestamp"] = new AttributeValue { S = DateTimeOffset.UnixEpoch.ToString("O") }
        };

    private static TransactionCanceledException ConflictCancelled(params string?[] reasonCodes)
    {
        var ex = new TransactionCanceledException("conflict");
        ex.CancellationReasons = reasonCodes
            .Select(code => new CancellationReason { Code = code })
            .ToList();
        return ex;
    }

    [Fact]
    public async Task Append_WritesOneConditionalPutPerEvent_AndReturnsNewVersion()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        TransactWriteItemsRequest? captured = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var newVersion = await store.AppendAsync("acct-1", expectedVersion: 0,
            new[] { new EventEnvelope("Opened", "{}"), new EventEnvelope("Debited", "{}") });

        Assert.Equal(2, newVersion);
        Assert.Equal(2, captured!.TransactItems.Count);
        Assert.Equal("attribute_not_exists(#pk)", captured.TransactItems[0].Put.ConditionExpression);
        Assert.Equal("1", captured.TransactItems[0].Put.Item["version"].N);
        Assert.Equal("2", captured.TransactItems[1].Put.Item["version"].N);
        Assert.False(string.IsNullOrEmpty(captured.ClientRequestToken));
    }

    [Fact]
    public async Task Append_WhenTransactionCancelledByAConditionalCheckFailure_ThrowsConcurrency_WithActualVersionAndInnerException()
    {
        var cancelled = ConflictCancelled("ConditionalCheckFailed");
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelled);
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { EventItem("acct-1", 3, "Latest") } });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var ex = await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, new[] { new EventEnvelope("Debited", "{}") }));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(3, ex.ActualVersion);
        Assert.Same(cancelled, ex.InnerException);   // #123
    }

    [Fact]
    public async Task Append_WhenTransactionCancelledForAThrottlingReason_RethrowsTheOriginalException()
    {
        // #122 — throttling/capacity/validation failures must not be mislabeled as a concurrency
        // conflict; the caller needs to see (and can react to) the real failure.
        var throttled = ConflictCancelled("ThrottlingError");
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(throttled);
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var thrown = await Assert.ThrowsAsync<TransactionCanceledException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, new[] { new EventEnvelope("Debited", "{}") }));

        Assert.Same(throttled, thrown);
        // No QueryAsync call should ever have been attempted (MockBehavior.Strict would already
        // throw if one had been, but assert explicitly for clarity).
        dynamo.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Append_WhenConflictDiagnosticReadFails_FallsBackToUnknownActualVersion()
    {
        // #124 — a failure in the post-conflict "actual version" read-back (e.g. throttled) must not
        // replace the genuine conflict with an unrelated exception.
        var cancelled = ConflictCancelled("ConditionalCheckFailed");
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelled);
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var ex = await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, new[] { new EventEnvelope("Debited", "{}") }));

        Assert.Equal(-1, ex.ActualVersion);
        Assert.Same(cancelled, ex.InnerException);
    }

    [Fact]
    public async Task Append_WhenConflictDiagnosticRead_IgnoresTheCallersCancellationToken()
    {
        // #124 — the diagnostic read-back must run on its own CancellationToken.None, not the
        // caller's token, so a raced cancellation can't replace the conflict with an OCE.
        var cancelled = ConflictCancelled("ConditionalCheckFailed");
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelled);
        CancellationToken? capturedToken = null;
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((_, ct) => capturedToken = ct)
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        var store = new DynamoDbEventStore(dynamo.Object, "events");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, new[] { new EventEnvelope("Debited", "{}") }, cts.Token));

        Assert.Equal(CancellationToken.None, capturedToken);
    }

    [Fact]
    public async Task Append_WithExpectedVersionGreaterThanZero_IncludesAConditionCheckOnTheExpectedVersion()
    {
        // #121 — without this, an expectedVersion ahead of the real head would find its target Put
        // slots free and silently gap the stream.
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        TransactWriteItemsRequest? captured = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await store.AppendAsync("acct-1", expectedVersion: 5, new[] { new EventEnvelope("Debited", "{}") });

        Assert.Equal(2, captured!.TransactItems.Count);
        var check = captured.TransactItems[0].ConditionCheck;
        Assert.NotNull(check);
        Assert.Equal("attribute_exists(#pk)", check.ConditionExpression);
        Assert.Equal("5", check.Key["version"].N);
        Assert.Equal("acct-1", check.Key["pk"].S);
        // The Put for the new event follows, still targeting expectedVersion + 1.
        Assert.Equal("6", captured.TransactItems[1].Put.Item["version"].N);
    }

    [Fact]
    public async Task Append_WithExpectedVersionZero_OmitsTheConditionCheck()
    {
        // A brand-new stream has no (streamId, 0) item to assert exists — the Put's own
        // attribute_not_exists(version 1) is the only concurrency check needed here.
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        TransactWriteItemsRequest? captured = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await store.AppendAsync("acct-1", expectedVersion: 0, new[] { new EventEnvelope("Opened", "{}") });

        Assert.Single(captured!.TransactItems);
        Assert.NotNull(captured.TransactItems[0].Put);
    }

    [Fact]
    public async Task Append_WithANegativeExpectedVersion_Throws()
    {
        // #121
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.AppendAsync("acct-1", expectedVersion: -1, new[] { new EventEnvelope("Debited", "{}") }));
    }

    [Fact]
    public async Task Append_EmptyBatch_StillChecksConcurrency_AndThrowsOnMismatch()
    {
        // #128 — an empty append must not silently bypass the concurrency check (matching
        // InMemoryEventStore, which always checks even for an empty batch).
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { EventItem("acct-1", 4, "Latest") } });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var ex = await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, Array.Empty<EventEnvelope>()));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(4, ex.ActualVersion);
        dynamo.Verify(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Append_EmptyBatch_ReturnsExpectedVersionWhenItMatchesTheHead()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { EventItem("acct-1", 4, "Latest") } });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var version = await store.AppendAsync("acct-1", expectedVersion: 4, Array.Empty<EventEnvelope>());

        Assert.Equal(4, version);
    }

    [Fact]
    public async Task Append_MoreThanTransactionLimit_Throws()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var store = new DynamoDbEventStore(dynamo.Object, "events");
        var tooMany = Enumerable.Range(0, 101).Select(_ => new EventEnvelope("E", "{}")).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync("acct-1", 0, tooMany));
    }

    [Fact]
    public async Task Append_ExactlyTheTransactionLimit_AtExpectedVersionZero_Succeeds()
    {
        // #271 — a brand-new stream (expectedVersion == 0) has no ConditionCheck item, so a genuine
        // 100-event append is exactly 100 transact items and must still succeed.
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        TransactWriteItemsRequest? captured = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DynamoDbEventStore(dynamo.Object, "events");
        var exactly100 = Enumerable.Range(0, 100).Select(_ => new EventEnvelope("E", "{}")).ToArray();

        var newVersion = await store.AppendAsync("acct-1", 0, exactly100);

        Assert.Equal(100, newVersion);
        Assert.Equal(100, captured!.TransactItems.Count);
    }

    [Fact]
    public async Task Append_100EventsAtAnExpectedVersionGreaterThanZero_ThrowsAFriendlyErrorPreFlight()
    {
        // #271 — an append onto an EXISTING stream (expectedVersion > 0) also carries the #121
        // ConditionCheck item, so 100 events + 1 condition check = 101 transact items, over AWS's
        // hard 100-item limit. The library must reject this itself with a friendly ArgumentException
        // before ever calling DynamoDB (MockBehavior.Strict enforces no SDK call is made).
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var store = new DynamoDbEventStore(dynamo.Object, "events");
        var exactly100 = Enumerable.Range(0, 100).Select(_ => new EventEnvelope("E", "{}")).ToArray();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync("acct-1", expectedVersion: 5, exactly100));

        Assert.Contains("99", ex.Message);
        dynamo.Verify(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Append_99EventsAtAnExpectedVersionGreaterThanZero_ProducesExactly100TransactItems_AndSucceeds()
    {
        // #271 — 99 events + 1 ConditionCheck item = 100 transact items, exactly at (not over) AWS's
        // limit, so this must succeed.
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        TransactWriteItemsRequest? captured = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DynamoDbEventStore(dynamo.Object, "events");
        var exactly99 = Enumerable.Range(0, 99).Select(_ => new EventEnvelope("E", "{}")).ToArray();

        var newVersion = await store.AppendAsync("acct-1", expectedVersion: 5, exactly99);

        Assert.Equal(104, newVersion);
        Assert.Equal(100, captured!.TransactItems.Count);
    }

    [Fact]
    public async Task Append_SameStreamExpectedVersionAndEvents_ProducesTheSameClientRequestToken()
    {
        // #129 — a deterministic token means a retried request for the exact same append is treated
        // by DynamoDB as the same attempt, not a fresh (potentially conflicting) one.
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var captured = new List<string>();
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured.Add(r.ClientRequestToken))
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await store.AppendAsync("acct-1", 0, new[] { new EventEnvelope("Opened", "{\"a\":1}") });
        await store.AppendAsync("acct-1", 0, new[] { new EventEnvelope("Opened", "{\"a\":1}") });
        await store.AppendAsync("acct-1", 0, new[] { new EventEnvelope("Opened", "{\"a\":2}") });

        Assert.Equal(2, captured.Distinct().Count());
        Assert.Equal(captured[0], captured[1]);
        Assert.NotEqual(captured[0], captured[2]);
    }

    [Fact]
    public async Task Read_ReturnsEventsInVersionOrder()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    EventItem("acct-1", 1, "Opened"),
                    EventItem("acct-1", 2, "Debited")
                },
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var events = await store.ReadAsync("acct-1");

        Assert.Equal(new[] { "Opened", "Debited" }, events.Select(e => e.EventType));
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.Version));
    }

    [Theory]
    [InlineData("eventType")]
    [InlineData("payload")]
    public async Task Read_WhenAnEventItemIsMissingARequiredAttribute_Throws(string missingAttribute)
    {
        // #127 — a missing/wrong-type eventType or payload means the item is corrupt; silently
        // defaulting to string.Empty would hand the caller a fabricated event instead.
        var item = EventItem("acct-1", 1, "Opened");
        item.Remove(missingAttribute);
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("acct-1"));
    }

    [Fact]
    public async Task Read_WhenEventTypeIsNotAStringAttribute_Throws()
    {
        // #127 — a non-S type (e.g. N) leaves AttributeValue.S null; that must not silently become
        // an empty-string EventType.
        var item = EventItem("acct-1", 1, "Opened");
        item["eventType"] = new AttributeValue { N = "1" };
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("acct-1"));
    }

    [Fact]
    public async Task Read_UsesConsistentRead_SoARehydrateImmediatelyAfterAnAppendCannotMissIt()
    {
        // DynamoDB Query defaults to eventually consistent. A command handler's normal cycle is
        // rehydrate (ReadAsync) -> decide -> AppendAsync with the version it just read; an
        // eventually-consistent read that misses the most recent append would let the handler
        // decide against stale state (the follow-on AppendAsync's own conditional Put would then
        // often - but not always, since it depends on what else the caller's decision already did -
        // surface the staleness as a spurious concurrency conflict, not silently succeed wrongly).
        // This store's sibling read paths (DynamoDbIdempotencyStore/DynamoDbOutboxStore) both request
        // ConsistentRead for exactly this reason - ReadAsync must match that discipline.
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        QueryRequest? captured = null;
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await store.ReadAsync("acct-1");

        Assert.True(captured!.ConsistentRead);
    }

    [Fact]
    public async Task Append_WhenTransactionCancelled_ReportsActualVersionViaConsistentRead()
    {
        // The actual-version lookup after a cancelled transaction must be just as consistent as the
        // append it's diagnosing - an eventually-consistent read here could report an ActualVersion
        // that is itself stale, misleading a caller that retries based on it.
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ConflictCancelled("ConditionalCheckFailed"));
        QueryRequest? captured = null;
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { EventItem("acct-1", 3, "Latest") } });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, new[] { new EventEnvelope("Debited", "{}") }));

        Assert.True(captured!.ConsistentRead);
    }

    [Fact]
    public void Constructor_WithANullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DynamoDbEventStore(null!, "events"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithAnInvalidTableName_Throws(string? tableName)
    {
        // #126
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);

        Assert.Throws<ArgumentException>(() => new DynamoDbEventStore(dynamo.Object, tableName!));
    }

    [Fact]
    public void Constructor_WithTheSamePartitionAndSortKeyAttribute_Throws()
    {
        // #126
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);

        Assert.Throws<ArgumentException>(() =>
            new DynamoDbEventStore(dynamo.Object, "events", partitionKeyAttribute: "id", sortKeyAttribute: "id"));
    }

    [Theory]
    [InlineData("eventType")]
    [InlineData("payload")]
    [InlineData("timestamp")]
    public void Constructor_WithAPartitionKeyCollidingWithAReservedAttribute_Throws(string reserved)
    {
        // #126
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);

        Assert.Throws<ArgumentException>(() =>
            new DynamoDbEventStore(dynamo.Object, "events", partitionKeyAttribute: reserved));
    }

    [Theory]
    [InlineData("eventType")]
    [InlineData("payload")]
    [InlineData("timestamp")]
    public void Constructor_WithASortKeyCollidingWithAReservedAttribute_Throws(string reserved)
    {
        // #126
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);

        Assert.Throws<ArgumentException>(() =>
            new DynamoDbEventStore(dynamo.Object, "events", sortKeyAttribute: reserved));
    }
}
