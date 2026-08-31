using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Benzene.Idempotency.DynamoDb;

/// <summary>
/// A distributed <see cref="IIdempotencyStore"/> backed by an Amazon DynamoDB table. Unlike
/// <c>InMemoryIdempotencyStore</c>, this is safe across a fleet of instances (Lambdas, containers):
/// the first-time claim is an atomic conditional write, so concurrent redeliveries of the same
/// message cannot both win.
/// </summary>
/// <remarks>
/// <para>
/// The table needs a single string partition-key attribute (default <c>pk</c>). Enable DynamoDB TTL
/// on the <c>expiresAt</c> attribute so records self-expire after the store's time-to-live — the
/// record only needs to outlive the transport's maximum redelivery window. Because DynamoDB's TTL
/// deletion is not immediate, the store also treats a record whose <c>expiresAt</c> is in the past as
/// absent when it reads one, so an expired key is reclaimable the instant it lapses.
/// </para>
/// <para>
/// The consumer registers <see cref="IAmazonDynamoDB"/> itself (this package resolves it); it does not
/// create the table.
/// </para>
/// <para>
/// <b>Claim fencing.</b> Every winning <see cref="TryClaimAsync"/> mints a fresh opaque claim token,
/// stored as the <c>claimToken</c> attribute alongside the record. <see cref="CompleteAsync"/> and
/// <see cref="ReleaseAsync"/> require the caller's token to be presented back and write with a
/// <c>ConditionExpression</c> that checks <c>claimToken</c> equality; a <see cref="ConditionalCheckFailedException"/>
/// (record gone, or reclaimed by another worker so <c>claimToken</c> has moved on) becomes a
/// <see langword="false"/> return rather than an exception, and nothing is written. This closes the
/// stale-writer-clobbers-the-new-holder hole a bare key-only settle API would have.
/// </para>
/// </remarks>
public class DynamoDbIdempotencyStore : IIdempotencyStore
{
    private const string StatusInProgress = "InProgress";
    private const string StatusCompleted = "Completed";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly string _partitionKeyAttribute;
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDbIdempotencyStore"/> class.
    /// </summary>
    /// <param name="dynamoDb">The DynamoDB client.</param>
    /// <param name="tableName">The table that stores idempotency records.</param>
    /// <param name="timeToLive">How long a record lives before a key can be reclaimed. Defaults to 24 hours; must exceed the transport's maximum redelivery window.</param>
    /// <param name="partitionKeyAttribute">The table's string partition-key attribute name. Defaults to <c>pk</c>.</param>
    /// <param name="now">Clock, injectable for testing. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public DynamoDbIdempotencyStore(
        IAmazonDynamoDB dynamoDb,
        string tableName,
        TimeSpan? timeToLive = null,
        string partitionKeyAttribute = "pk",
        Func<DateTimeOffset>? now = null)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _partitionKeyAttribute = partitionKeyAttribute;
        _timeToLive = timeToLive ?? TimeSpan.FromHours(24);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Bound on how many times <see cref="TryClaimAsync"/> retries the conditional <c>PutItem</c>
    /// after a <c>ConditionalCheckFailedException</c> whose immediate read-back finds the record
    /// absent (a race with a concurrent delete/release). Small, because a genuinely contended key
    /// should resolve to a live record within a couple of tries; see <see cref="IdempotencyClaimContentionException"/>.
    /// </summary>
    private const int MaxClaimAttempts = 3;

    /// <inheritdoc />
    public async Task<ClaimResult> TryClaimAsync(string key, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            var now = _now();
            var claimToken = Guid.NewGuid().ToString();
            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    [_partitionKeyAttribute] = new AttributeValue { S = key },
                    ["status"] = new AttributeValue { S = StatusInProgress },
                    ["wasSuccessful"] = new AttributeValue { BOOL = false },
                    ["expiresAt"] = new AttributeValue { N = ToEpochSeconds(now + _timeToLive) },
                    ["claimToken"] = new AttributeValue { S = claimToken }
                },
                // Win the claim only when there is no live record: either nothing is there, or what's
                // there has already expired (DynamoDB TTL deletion lags, so check it explicitly).
                // Inclusive (<=) to match ReadRecordAsync's own expiry check below (#272) — a record
                // whose expiresAt equals "now" exactly must be reclaimable by both paths, not treated
                // as still live by the write and already-gone by the read.
                ConditionExpression = "attribute_not_exists(#pk) OR expiresAt <= :now",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = _partitionKeyAttribute },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":now"] = new AttributeValue { N = ToEpochSeconds(now) }
                }
            };

            try
            {
                await _dynamoDb.PutItemAsync(request, cancellationToken);
                // Every Won corresponds to this successful write — never synthesized.
                return ClaimResult.Won(claimToken);
            }
            catch (ConditionalCheckFailedException)
            {
                // A live record existed at write time — read it back so the middleware can act on its
                // outcome.
                var existing = await ReadRecordAsync(key, now, cancellationToken);
                if (existing != null)
                {
                    return ClaimResult.AlreadyExists(existing);
                }

                // Negligible race: the record was live when the condition was evaluated but had
                // become absent by the time we read it back (e.g. a concurrent ReleaseAsync deleted
                // it in between). We must NOT synthesize a Won here — nothing has been written yet.
                // Retry the conditional PutItem against this now-observed-absent state instead; a
                // further retry could lose the same race again, so this is bounded.
            }
        }

        // Every attempt raced against a live record that had vanished by the time we read it back,
        // and every retry still lost. Never fabricate a Won with no durable write behind it — surface
        // the contention instead.
        throw new IdempotencyClaimContentionException(key, MaxClaimAttempts);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(string key, string claimToken, bool wasSuccessful, CancellationToken cancellationToken = default)
    {
        var now = _now();
        return await TryWriteIfTokenMatchesAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                [_partitionKeyAttribute] = new AttributeValue { S = key },
                ["status"] = new AttributeValue { S = StatusCompleted },
                ["wasSuccessful"] = new AttributeValue { BOOL = wasSuccessful },
                ["expiresAt"] = new AttributeValue { N = ToEpochSeconds(now + _timeToLive) },
                ["claimToken"] = new AttributeValue { S = claimToken }
            },
            ConditionExpression = "claimToken = :claimToken",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":claimToken"] = new AttributeValue { S = claimToken }
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken = default)
    {
        try
        {
            await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue> { [_partitionKeyAttribute] = new AttributeValue { S = key } },
                ConditionExpression = "claimToken = :claimToken",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":claimToken"] = new AttributeValue { S = claimToken }
                }
            }, cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            // No live record, or its claimToken no longer matches (reclaimed by another worker, or
            // already settled) — refuse rather than deleting whatever the current holder has.
            return false;
        }
    }

    /// <summary>
    /// Issues a settle <c>PutItem</c> whose <see cref="PutItemRequest.ConditionExpression"/> is
    /// already scoped to the presented claim token, translating a condition failure into
    /// <see langword="false"/> (fenced out) rather than an exception.
    /// </summary>
    private async Task<bool> TryWriteIfTokenMatchesAsync(PutItemRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _dynamoDb.PutItemAsync(request, cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private async Task<IdempotencyRecord?> ReadRecordAsync(string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { [_partitionKeyAttribute] = new AttributeValue { S = key } },
            ConsistentRead = true
        }, cancellationToken);

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

        // Treat a lapsed-but-not-yet-deleted record as absent, so an expired key is reclaimable.
        if (response.Item.TryGetValue("expiresAt", out var expiresAt)
            && long.TryParse(expiresAt.N, out var epoch)
            && epoch <= now.ToUnixTimeSeconds())
        {
            return null;
        }

        var status = response.Item.TryGetValue("status", out var s) && s.S == StatusCompleted
            ? IdempotencyStatus.Completed
            : IdempotencyStatus.InProgress;
        var wasSuccessful = response.Item.TryGetValue("wasSuccessful", out var w) && w.BOOL == true;

        return new IdempotencyRecord(key, status, wasSuccessful);
    }

    private static string ToEpochSeconds(DateTimeOffset value)
        => value.ToUnixTimeSeconds().ToString();
}
