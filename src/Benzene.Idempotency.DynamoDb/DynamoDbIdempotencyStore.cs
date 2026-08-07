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

    /// <inheritdoc />
    public async Task<ClaimResult> TryClaimAsync(string key, CancellationToken cancellationToken = default)
    {
        var now = _now();
        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                [_partitionKeyAttribute] = new AttributeValue { S = key },
                ["status"] = new AttributeValue { S = StatusInProgress },
                ["wasSuccessful"] = new AttributeValue { BOOL = false },
                ["expiresAt"] = new AttributeValue { N = ToEpochSeconds(now + _timeToLive) }
            },
            // Win the claim only when there is no live record: either nothing is there, or what's
            // there has already expired (DynamoDB TTL deletion lags, so check it explicitly).
            ConditionExpression = "attribute_not_exists(#pk) OR expiresAt < :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = _partitionKeyAttribute },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":now"] = new AttributeValue { N = ToEpochSeconds(now) }
            }
        };

        try
        {
            await _dynamoDb.PutItemAsync(request, cancellationToken);
            return ClaimResult.Won();
        }
        catch (ConditionalCheckFailedException)
        {
            // A live record already exists — read it back so the middleware can act on its outcome.
            var existing = await ReadRecordAsync(key, now, cancellationToken);
            // Negligible race: it lapsed between the condition check and the read. Treat as claimable
            // (a possible re-process) rather than falsely reporting a duplicate (which would drop a message).
            return existing != null ? ClaimResult.AlreadyExists(existing) : ClaimResult.Won();
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string key, bool wasSuccessful, CancellationToken cancellationToken = default)
    {
        var now = _now();
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                [_partitionKeyAttribute] = new AttributeValue { S = key },
                ["status"] = new AttributeValue { S = StatusCompleted },
                ["wasSuccessful"] = new AttributeValue { BOOL = wasSuccessful },
                ["expiresAt"] = new AttributeValue { N = ToEpochSeconds(now + _timeToLive) }
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { [_partitionKeyAttribute] = new AttributeValue { S = key } }
        }, cancellationToken);
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
