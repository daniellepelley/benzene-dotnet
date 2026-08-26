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
    public async Task TryClaim_WhenExistingRecordHasExpired_RetriesThePutAndWins()
    {
        // The conditional write raced (we saw ConditionalCheckFailed) but the record read back is
        // already past its TTL, so the store treats the read-back as absent -- which means it must
        // retry the conditional PutItem (never synthesize a Won) and only report Won once that retry
        // actually writes.
        var dynamo = MockDynamo();
        var attempt = 0;
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutItemRequest, CancellationToken>((_, _) =>
            {
                attempt++;
                return attempt == 1
                    ? throw new ConditionalCheckFailedException("stale")
                    : Task.FromResult(new PutItemResponse());
            });
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LiveRecord("key-1", "InProgress", false, Now.AddHours(-1)));
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var claim = await store.TryClaimAsync("key-1");

        Assert.True(claim.Claimed);
        Assert.Equal(2, attempt);
        dynamo.Verify(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TryClaim_WhenReadBackAfterConflictFindsRecordGone_RetriesThePut_AndOnlyWinsAfterItSucceeds()
    {
        // Regression test for #31 (phantom win): the first PutItem loses the race (a live record
        // exists), but by the time we read it back it's gone (e.g. a concurrent ReleaseAsync deleted
        // it). The store must NOT synthesize a Won from that empty read -- it must retry the
        // conditional PutItem, and only return Won once that retry actually writes.
        var dynamo = MockDynamo();
        var putCalls = new List<PutItemRequest>();
        var putAttempt = 0;
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutItemRequest, CancellationToken>((r, _) =>
            {
                putAttempt++;
                putCalls.Add(r);
                return putAttempt == 1
                    ? throw new ConditionalCheckFailedException("live record exists")
                    : Task.FromResult(new PutItemResponse());
            });
        var getCalls = 0;
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => getCalls++)
            .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() }); // absent
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var claim = await store.TryClaimAsync("key-1");

        Assert.True(claim.Claimed);
        Assert.NotNull(claim.ClaimToken);
        Assert.Equal(2, putAttempt);
        Assert.Equal(1, getCalls);
        // The winning write is the SECOND PutItem (the retry), carrying the same token returned.
        Assert.Equal(claim.ClaimToken, putCalls[1].Item["claimToken"].S);
        dynamo.Verify(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        dynamo.Verify(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryClaim_WhenRetryAlsoLosesToALiveRecord_ReturnsAlreadyExists_NotAPhantomWin()
    {
        // The first PutItem loses, the read-back finds it gone (the #31 race), so we retry -- but the
        // retry ALSO loses, and this time the read-back finds a genuinely live record (someone else
        // won in between). This must surface as AlreadyExists, never a synthesized Won.
        var dynamo = MockDynamo();
        var putAttempt = 0;
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutItemRequest, CancellationToken>((_, _) =>
            {
                putAttempt++;
                throw new ConditionalCheckFailedException("live record exists");
            });
        var getAttempt = 0;
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetItemRequest, CancellationToken>((_, _) =>
            {
                getAttempt++;
                return Task.FromResult(getAttempt == 1
                    ? new GetItemResponse { Item = new Dictionary<string, AttributeValue>() } // absent
                    : LiveRecord("key-1", "InProgress", false, Now.AddHours(1))); // now genuinely live
            });
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var claim = await store.TryClaimAsync("key-1");

        Assert.False(claim.Claimed);
        Assert.Null(claim.ClaimToken);
        Assert.NotNull(claim.ExistingRecord);
        Assert.Equal(2, putAttempt);
        Assert.Equal(2, getAttempt);
    }

    [Fact]
    public async Task TryClaim_WhenEveryRetryRacesAgainstAVanishingRecord_ThrowsRatherThanPhantomWin()
    {
        // Pathological case: every conditional PutItem loses, and every immediate read-back finds the
        // record absent -- persistent contention that never resolves to either a successful write or
        // a stable live record within the retry cap. Must not return Won (nothing was ever written)
        // and must not return AlreadyExists (there is no record to report).
        var dynamo = MockDynamo();
        var putAttempt = 0;
        dynamo.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutItemRequest, CancellationToken>((_, _) =>
            {
                putAttempt++;
                throw new ConditionalCheckFailedException("live record exists");
            });
        dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() }); // always absent
        var store = new DynamoDbIdempotencyStore(dynamo.Object, "idempotency", now: () => Now);

        var ex = await Assert.ThrowsAsync<IdempotencyClaimContentionException>(() => store.TryClaimAsync("key-1"));

        Assert.Equal("key-1", ex.Key);
        Assert.Equal(3, putAttempt);
        dynamo.Verify(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        dynamo.Verify(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
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
