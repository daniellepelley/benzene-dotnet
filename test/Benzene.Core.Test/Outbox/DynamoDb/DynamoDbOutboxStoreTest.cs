using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.Outbox;
using Benzene.Outbox.DynamoDb;
using Moq;
using Xunit;

namespace Benzene.Test.Outbox.DynamoDb;

public class DynamoDbOutboxStoreTest
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Mock<IAmazonDynamoDB> MockDynamo() => new(MockBehavior.Strict);

    private static Dictionary<string, AttributeValue> PendingItem(string id, DateTimeOffset createdAtUtc, DateTimeOffset? nextAttemptAtUtc = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["id"] = new AttributeValue { S = id },
            ["topic"] = new AttributeValue { S = "payments:capture" },
            ["payload"] = new AttributeValue { S = "\"payload\"" },
            ["payloadType"] = new AttributeValue { S = typeof(string).AssemblyQualifiedName! },
            ["headers"] = new AttributeValue { M = new Dictionary<string, AttributeValue>() },
            ["createdAtUtc"] = new AttributeValue { S = createdAtUtc.UtcDateTime.ToString("o") },
            ["attemptCount"] = new AttributeValue { N = "0" },
            ["status"] = new AttributeValue { S = "Pending" },
            ["gsiPk"] = new AttributeValue { S = "pending" },
            ["gsiSk"] = new AttributeValue { S = (nextAttemptAtUtc ?? createdAtUtc).UtcDateTime.ToString("o") }
        };

        if (nextAttemptAtUtc.HasValue)
        {
            item["nextAttemptAtUtc"] = new AttributeValue { S = nextAttemptAtUtc.Value.UtcDateTime.ToString("o") };
        }

        return item;
    }

    [Fact]
    public async Task AddAsync_PutsOneItemPerEnvelope()
    {
        var dynamo = MockDynamo();
        var puts = new List<PutItemRequest>();
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((r, _) => puts.Add(r))
            .ReturnsAsync(new PutItemResponse());
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        var envelope = new OutboxEnvelope("env-1", "payments:capture", "\"p\"", typeof(string).AssemblyQualifiedName!, new Dictionary<string, string>(), Now);
        await store.AddAsync([envelope]);

        var put = Assert.Single(puts);
        Assert.Equal("outbox", put.TableName);
        Assert.Equal("env-1", put.Item["id"].S);
        Assert.Equal("pending", put.Item["gsiPk"].S);
    }

    [Fact]
    public async Task ClaimDue_QueriesPendingIndex_AndClaimsEachItemWithConditionalUpdate()
    {
        var dynamo = MockDynamo();
        QueryRequest? query = null;
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((r, _) => query = r)
            .ReturnsAsync(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { PendingItem("env-1", Now.AddMinutes(-5)) }
            });
        UpdateItemRequest? update = null;
        dynamo.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((r, _) => update = r)
            .ReturnsAsync(new UpdateItemResponse());
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(2));

        Assert.Equal("pending-index", query!.IndexName);
        var claimed = Assert.Single(due);
        Assert.Equal("env-1", claimed.Id);
        Assert.Equal(OutboxStatus.Pending, claimed.Status);
        Assert.Contains("leaseUntil", update!.UpdateExpression);
        Assert.Contains("attribute_exists(#pk)", update.ConditionExpression);
    }

    [Fact]
    public async Task ClaimDue_WhenAnotherClaimerWinsTheRace_ExcludesThatItem()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    PendingItem("env-1", Now.AddMinutes(-5)),
                    PendingItem("env-2", Now.AddMinutes(-4))
                }
            });
        dynamo.SetupSequence(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("already leased"))
            .ReturnsAsync(new UpdateItemResponse());
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(2));

        var claimed = Assert.Single(due);
        Assert.Equal("env-2", claimed.Id);
    }

    [Fact]
    public async Task Claim_WhenLiveLeaseExists_RefusesWithoutReadingTheItemBack()
    {
        // Strict mock: no GetItemAsync setup, so if the store tried to read the item back after a
        // refused claim, this test would fail on an unexpected invocation.
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("already leased"));
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        var claimed = await store.ClaimAsync("env-1", TimeSpan.FromMinutes(2));

        Assert.Null(claimed);
    }

    [Fact]
    public async Task Claim_WhenFree_WinsAndReturnsTheEnvelope()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateItemResponse());
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = PendingItem("env-1", Now.AddMinutes(-5)) });
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        var claimed = await store.ClaimAsync("env-1", TimeSpan.FromMinutes(2));

        Assert.NotNull(claimed);
        Assert.Equal("env-1", claimed!.Id);
    }

    [Fact]
    public async Task MarkDispatched_SetsExpiresAt_AndRemovesGsiAndLeaseAttributes()
    {
        var dynamo = MockDynamo();
        UpdateItemRequest? update = null;
        dynamo.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((r, _) => update = r)
            .ReturnsAsync(new UpdateItemResponse());
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", retentionPeriod: TimeSpan.FromDays(7), now: () => Now);

        await store.MarkDispatchedAsync("env-1");

        Assert.Equal("Dispatched", update!.ExpressionAttributeValues[":dispatched"].S);
        Assert.Equal(Now.AddDays(7).ToUnixTimeSeconds().ToString(), update.ExpressionAttributeValues[":expiresAt"].N);
        Assert.Contains("REMOVE", update.UpdateExpression);
        Assert.Contains("#gsiPk", update.UpdateExpression);
        Assert.Contains("#gsiSk", update.UpdateExpression);
        Assert.Contains("leaseUntil", update.UpdateExpression);
    }

    [Fact]
    public async Task MarkDispatched_WhenEnvelopeNoLongerExists_IsANoOp()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("missing"));
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        await store.MarkDispatchedAsync("missing-env");
        // No exception - a no-op, per the IOutboxStore contract.
    }

    [Fact]
    public async Task Reschedule_UpdatesAttemptCountNextAttemptAndGsiSortKey_AndReleasesLease()
    {
        var dynamo = MockDynamo();
        UpdateItemRequest? update = null;
        dynamo.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((r, _) => update = r)
            .ReturnsAsync(new UpdateItemResponse());
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        await store.RescheduleAsync("env-1", attemptCount: 2, delay: TimeSpan.FromMinutes(1), error: "boom");

        Assert.Equal("2", update!.ExpressionAttributeValues[":attemptCount"].N);
        Assert.Equal("boom", update.ExpressionAttributeValues[":error"].S);
        Assert.Contains("#gsiSk", update.UpdateExpression);
        Assert.Contains("REMOVE leaseUntil", update.UpdateExpression);
    }

    [Fact]
    public async Task Park_SetsParkedStatus_AndRemovesGsiAndLeaseAttributes()
    {
        var dynamo = MockDynamo();
        UpdateItemRequest? update = null;
        dynamo.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((r, _) => update = r)
            .ReturnsAsync(new UpdateItemResponse());
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        await store.ParkAsync("env-1", "gave up");

        Assert.Equal("Parked", update!.ExpressionAttributeValues[":parked"].S);
        Assert.Equal("gave up", update.ExpressionAttributeValues[":error"].S);
        Assert.Contains("#gsiPk", update.UpdateExpression);
        Assert.Contains("#gsiSk", update.UpdateExpression);
    }

    [Fact]
    public async Task DeleteDispatchedBefore_IsANoOp_NativeTtlOwnsRetention()
    {
        var dynamo = MockDynamo();
        var store = new DynamoDbOutboxStore(dynamo.Object, "outbox", now: () => Now);

        var deleted = await store.DeleteDispatchedBeforeAsync(Now);

        Assert.Equal(0, deleted);
        dynamo.VerifyNoOtherCalls();
    }
}
