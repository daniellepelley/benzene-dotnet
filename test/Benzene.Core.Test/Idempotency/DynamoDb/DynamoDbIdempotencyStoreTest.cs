using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.Idempotency;
using Benzene.Idempotency.DynamoDb;
using Moq;
using Xunit;

namespace Benzene.Test.Idempotency.DynamoDb;

public class DynamoDbIdempotencyStoreTest
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Mock<IAmazonDynamoDB> MockDynamo() => new(MockBehavior.Strict);

    private static GetItemResponse LiveRecord(string key, string status, bool wasSuccessful, DateTimeOffset expiresAt)
        => new()
        {
            Item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = key },
                ["status"] = new() { S = status },
                ["wasSuccessful"] = new() { BOOL = wasSuccessful },
                ["expiresAt"] = new() { N = expiresAt.ToUnixTimeSeconds().ToString() }
            }
        };

    [Fact]
    public async Task TryClaim_FirstTime_WritesInProgress_AndWins()
    {
        var dynamo = MockDynamo();
        PutItemRequest? put = null;
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((r, _) => put = r)
            .ReturnsAsync(new PutItemResponse());
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var claim = await store.TryClaimAsync("key-1");

        Assert.True(claim.Claimed);
        Assert.Null(claim.ExistingRecord);
        Assert.NotNull(claim.ClaimToken);
        Assert.Equal("InProgress", put!.Item["status"].S);
        Assert.Equal(claim.ClaimToken, put.Item["claimToken"].S);
        Assert.Contains("attribute_not_exists", put.ConditionExpression);
    }

    [Fact]
    public async Task TryClaim_WhenLiveRecordExists_IsRefusedWithThatRecord()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LiveRecord("key-1", "InProgress", false, Now.AddHours(1)));
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var claim = await store.TryClaimAsync("key-1");

        Assert.False(claim.Claimed);
        Assert.Equal(IdempotencyStatus.InProgress, claim.ExistingRecord!.Status);
    }

    [Fact]
    public async Task TryClaim_AfterComplete_IsRefusedWithCompletedOutcome()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LiveRecord("key-1", "Completed", true, Now.AddHours(1)));
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var claim = await store.TryClaimAsync("key-1");

        Assert.False(claim.Claimed);
        Assert.Equal(IdempotencyStatus.Completed, claim.ExistingRecord!.Status);
        Assert.True(claim.ExistingRecord.WasSuccessful);
    }

    [Fact]
    public async Task TryClaim_WhenExistingRecordHasExpired_IsClaimable()
    {
        // The conditional write raced (we saw ConditionalCheckFailed) but the record read back is
        // already past its TTL, so the store treats it as absent and lets the claim win.
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("stale"));
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LiveRecord("key-1", "InProgress", false, Now.AddHours(-1)));
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var claim = await store.TryClaimAsync("key-1");

        Assert.True(claim.Claimed);
    }

    [Fact]
    public async Task Complete_WritesCompletedWithOutcome_ConditionedOnClaimToken()
    {
        var dynamo = MockDynamo();
        PutItemRequest? put = null;
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((r, _) => put = r)
            .ReturnsAsync(new PutItemResponse());
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var settled = await store.CompleteAsync("key-1", "token-1", wasSuccessful: true);

        Assert.True(settled);
        Assert.Equal("Completed", put!.Item["status"].S);
        Assert.True(put.Item["wasSuccessful"].BOOL);
        Assert.Equal("token-1", put.Item["claimToken"].S);
        Assert.Contains("claimToken", put.ConditionExpression);
        Assert.Equal("token-1", put.ExpressionAttributeValues[":claimToken"].S);
    }

    [Fact]
    public async Task Complete_WhenClaimTokenNoLongerMatches_IsRefused_AndWritesNothing()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("stale token"));
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var settled = await store.CompleteAsync("key-1", "stale-token", wasSuccessful: true);

        Assert.False(settled);
    }

    [Fact]
    public async Task Release_DeletesTheKey_ConditionedOnClaimToken()
    {
        var dynamo = MockDynamo();
        DeleteItemRequest? del = null;
        dynamo.Setup(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteItemRequest, CancellationToken>((r, _) => del = r)
            .ReturnsAsync(new DeleteItemResponse());
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var released = await store.ReleaseAsync("key-1", "token-1");

        Assert.True(released);
        Assert.Equal("key-1", del!.Key["pk"].S);
        Assert.Contains("claimToken", del.ConditionExpression);
        Assert.Equal("token-1", del.ExpressionAttributeValues[":claimToken"].S);
    }

    [Fact]
    public async Task Release_WhenClaimTokenNoLongerMatches_IsRefused()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("stale token"));
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var released = await store.ReleaseAsync("key-1", "stale-token");

        Assert.False(released);
    }
}
