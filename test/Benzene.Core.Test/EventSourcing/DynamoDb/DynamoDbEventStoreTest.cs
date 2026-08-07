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
    }

    [Fact]
    public async Task Append_WhenTransactionCancelled_ThrowsConcurrency_WithActualVersion()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransactionCanceledException("version taken"));
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { EventItem("acct-1", 3, "Latest") } });
        var store = new DynamoDbEventStore(dynamo.Object, "events");

        var ex = await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, new[] { new EventEnvelope("Debited", "{}") }));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(3, ex.ActualVersion);
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

    [Fact]
    public async Task Append_MoreThanTransactionLimit_Throws()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var store = new DynamoDbEventStore(dynamo.Object, "events");
        var tooMany = Enumerable.Range(0, 101).Select(_ => new EventEnvelope("E", "{}")).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync("acct-1", 0, tooMany));
    }
}
