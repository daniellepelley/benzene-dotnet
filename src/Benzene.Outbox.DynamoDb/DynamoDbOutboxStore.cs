using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Benzene.Outbox.DynamoDb;

/// <summary>
/// A distributed <see cref="IOutboxStore"/> backed by an Amazon DynamoDB table. Unlike
/// <c>InMemoryOutboxStore</c>, this is safe across a fleet of instances (Lambdas, containers): every
/// claim is an atomic conditional <c>UpdateItem</c>, so a stream-triggered dispatcher and a sweeper
/// racing the same envelope cannot both win it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Table shape.</b> Partition key: a single string attribute holding the envelope's
/// <see cref="OutboxEnvelope.Id"/> (default attribute name <c>id</c>). A <b>sparse</b> global
/// secondary index drives the sweeper (<see cref="ClaimDueAsync"/>): partition key <c>gsiPk</c>
/// (always the constant literal <c>"pending"</c>), sort key <c>gsiSk</c> (the envelope's due time -
/// <c>nextAttemptAtUtc</c>, or <c>createdAtUtc</c> for a freshly captured envelope that has never been
/// attempted). Both <c>gsiPk</c>/<c>gsiSk</c> are written only while an envelope is
/// <see cref="OutboxStatus.Pending"/> and removed by <see cref="MarkDispatchedAsync"/>/
/// <see cref="ParkAsync"/> - the index only ever holds envelopes still waiting to go out, so it stays
/// small regardless of how many envelopes have ever been dispatched. The index MUST project
/// <c>ProjectionType.ALL</c> so a query against it returns the full item (this store never issues a
/// second lookup per queried envelope). A constant-partition GSI serializes sweep throughput - fine
/// for this feature's throughput class; the DynamoDB-Streams dispatch path (a service's own
/// <c>Benzene.Aws.Lambda.DynamoDb</c> handler calling <see cref="ClaimAsync"/>) bypasses this index
/// entirely.
/// </para>
/// <para>
/// <b>TTL / retention.</b> Enable DynamoDB TTL on the <c>expiresAt</c> attribute (epoch seconds).
/// It is set only by <see cref="MarkDispatchedAsync"/> (never at capture, never on
/// <see cref="OutboxStatus.Parked"/>), so a <see cref="OutboxStatus.Dispatched"/> envelope self-expires
/// after <c>retentionPeriod</c> and a <see cref="OutboxStatus.Parked"/> one is never auto-deleted -
/// matching the promise <c>Benzene.Outbox/CLAUDE.md</c> makes ("parked is the operator's evidence").
/// Because native TTL owns retention here, <see cref="DeleteDispatchedBeforeAsync"/> is a no-op.
/// </para>
/// <para>
/// The consumer registers <see cref="IAmazonDynamoDB"/> itself (this package resolves it) and
/// provisions the table (including the GSI and TTL) - this store never creates it.
/// </para>
/// <para>
/// <b>Claim fencing.</b> Every winning claim (<see cref="ClaimDueAsync"/>/<see cref="ClaimAsync"/>)
/// writes a freshly minted <c>leaseToken</c> attribute alongside <c>leaseUntil</c>, and stamps it onto
/// the returned <see cref="OutboxEnvelope.LeaseToken"/>. <see cref="MarkDispatchedAsync"/>/
/// <see cref="RescheduleAsync"/>/<see cref="ParkAsync"/> extend their <c>ConditionExpression</c> with
/// <c>leaseToken = :leaseToken</c>, so a settle whose token no longer matches the live lease (reclaimed
/// by another claimer, or already settled) hits <see cref="ConditionalCheckFailedException"/> just
/// like the "envelope doesn't exist" case already did, and is reported back as <see langword="false"/>
/// rather than an exception - see <see cref="IOutboxStore.MarkDispatchedAsync"/>'s remarks for what
/// this closes and what it doesn't.
/// </para>
/// </remarks>
public class DynamoDbOutboxStore : IOutboxStore
{
    private const string StatusPending = "Pending";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly string _partitionKeyAttribute;
    private readonly string _pendingIndexName;
    private readonly TimeSpan _retentionPeriod;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDbOutboxStore"/> class.
    /// </summary>
    /// <param name="dynamoDb">The DynamoDB client.</param>
    /// <param name="tableName">The table that stores outbox envelopes.</param>
    /// <param name="retentionPeriod">How long a <see cref="OutboxStatus.Dispatched"/> envelope lives before the native TTL removes it. Defaults to 7 days - must be reflected in the table's TTL configuration; this value only decides what <c>expiresAt</c> is set to.</param>
    /// <param name="partitionKeyAttribute">The table's string partition-key attribute name. Defaults to <c>id</c>.</param>
    /// <param name="pendingIndexName">The sparse GSI's name (see the class remarks). Defaults to <c>pending-index</c>.</param>
    /// <param name="now">Clock, injectable for testing. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public DynamoDbOutboxStore(
        IAmazonDynamoDB dynamoDb,
        string tableName,
        TimeSpan? retentionPeriod = null,
        string partitionKeyAttribute = "id",
        string pendingIndexName = "pending-index",
        Func<DateTimeOffset>? now = null)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _retentionPeriod = retentionPeriod ?? TimeSpan.FromDays(7);
        _partitionKeyAttribute = partitionKeyAttribute;
        _pendingIndexName = pendingIndexName;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task AddAsync(IEnumerable<OutboxEnvelope> envelopes, CancellationToken cancellationToken = default)
    {
        foreach (var envelope in envelopes)
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = DynamoDbOutboxItemMapper.ToItem(envelope, _partitionKeyAttribute)
            }, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxEnvelope>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = _now();

        var query = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            IndexName = _pendingIndexName,
            KeyConditionExpression = "#gsiPk = :pending AND #gsiSk <= :now",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#gsiPk"] = DynamoDbOutboxItemMapper.GsiPartitionKeyAttribute,
                ["#gsiSk"] = DynamoDbOutboxItemMapper.GsiSortKeyAttribute
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pending"] = new AttributeValue { S = DynamoDbOutboxItemMapper.GsiPendingValue },
                [":now"] = new AttributeValue { S = DynamoDbOutboxItemMapper.FormatTimestamp(now) }
            },
            Limit = batchSize,
            ScanIndexForward = true
        }, cancellationToken);

        var claimed = new List<OutboxEnvelope>(query.Items.Count);
        foreach (var item in query.Items)
        {
            var id = item[_partitionKeyAttribute].S;
            var token = await TryClaimAsync(id, now, lease, cancellationToken);
            if (token != null)
            {
                claimed.Add(DynamoDbOutboxItemMapper.ToEnvelope(item, _partitionKeyAttribute).WithLeaseToken(token));
            }
        }

        return claimed;
    }

    /// <inheritdoc />
    public async Task<OutboxEnvelope?> ClaimAsync(string id, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = _now();
        var token = await TryClaimAsync(id, now, lease, cancellationToken);
        if (token == null)
        {
            return null;
        }

        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = Key(id),
            ConsistentRead = true
        }, cancellationToken);

        return response.Item is { Count: > 0 }
            ? DynamoDbOutboxItemMapper.ToEnvelope(response.Item, _partitionKeyAttribute).WithLeaseToken(token)
            : null;
    }

    /// <inheritdoc />
    public async Task<bool> MarkDispatchedAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        var expiresAt = _now() + _retentionPeriod;

        return await TryUpdateAsync(id, new UpdateItemRequest
        {
            TableName = _tableName,
            Key = Key(id),
            UpdateExpression =
                "SET #status = :dispatched, expiresAt = :expiresAt " +
                "REMOVE #gsiPk, #gsiSk, leaseUntil, leaseToken, nextAttemptAtUtc",
            ConditionExpression = "attribute_exists(#pk) AND leaseToken = :leaseToken",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#pk"] = _partitionKeyAttribute,
                ["#status"] = "status",
                ["#gsiPk"] = DynamoDbOutboxItemMapper.GsiPartitionKeyAttribute,
                ["#gsiSk"] = DynamoDbOutboxItemMapper.GsiSortKeyAttribute
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":dispatched"] = new AttributeValue { S = "Dispatched" },
                [":expiresAt"] = new AttributeValue { N = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
                [":leaseToken"] = new AttributeValue { S = leaseToken }
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RescheduleAsync(string id, int attemptCount, TimeSpan delay, string error, string leaseToken, CancellationToken cancellationToken = default)
    {
        var next = _now() + delay;

        return await TryUpdateAsync(id, new UpdateItemRequest
        {
            TableName = _tableName,
            Key = Key(id),
            UpdateExpression =
                "SET attemptCount = :attemptCount, nextAttemptAtUtc = :next, lastError = :error, #gsiSk = :next " +
                "REMOVE leaseUntil, leaseToken",
            ConditionExpression = "attribute_exists(#pk) AND leaseToken = :leaseToken",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#pk"] = _partitionKeyAttribute,
                ["#gsiSk"] = DynamoDbOutboxItemMapper.GsiSortKeyAttribute
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":attemptCount"] = new AttributeValue { N = attemptCount.ToString(CultureInfo.InvariantCulture) },
                [":next"] = new AttributeValue { S = DynamoDbOutboxItemMapper.FormatTimestamp(next) },
                [":error"] = new AttributeValue { S = error },
                [":leaseToken"] = new AttributeValue { S = leaseToken }
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ParkAsync(string id, string error, string leaseToken, CancellationToken cancellationToken = default)
    {
        return await TryUpdateAsync(id, new UpdateItemRequest
        {
            TableName = _tableName,
            Key = Key(id),
            UpdateExpression =
                "SET #status = :parked, lastError = :error " +
                "REMOVE #gsiPk, #gsiSk, leaseUntil, leaseToken, nextAttemptAtUtc",
            ConditionExpression = "attribute_exists(#pk) AND leaseToken = :leaseToken",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#pk"] = _partitionKeyAttribute,
                ["#status"] = "status",
                ["#gsiPk"] = DynamoDbOutboxItemMapper.GsiPartitionKeyAttribute,
                ["#gsiSk"] = DynamoDbOutboxItemMapper.GsiSortKeyAttribute
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":parked"] = new AttributeValue { S = "Parked" },
                [":error"] = new AttributeValue { S = error },
                [":leaseToken"] = new AttributeValue { S = leaseToken }
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Always a no-op returning <c>0</c> - native DynamoDB TTL on <c>expiresAt</c> owns retention for this store (see the class remarks).</remarks>
    public Task<int> DeleteDispatchedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <summary>
    /// Attempts the atomic claim: a conditional <c>UpdateItem</c> setting <c>leaseUntil</c> and a
    /// freshly minted <c>leaseToken</c>, winning only when the envelope exists, is still
    /// <see cref="OutboxStatus.Pending"/>, is due (<c>nextAttemptAtUtc</c> absent or in the past), and
    /// is unleased or its lease has lapsed. This single conditional expression is the whole atomicity
    /// story - unlike a claim built on <c>PutItem</c> (which can only succeed or fail as a whole and
    /// therefore needs a read-back to tell "doesn't exist" apart from "live lease"), <c>UpdateItem</c>'s
    /// condition expression tests exactly the disjunction we need ("free OR lapsed") in the same round
    /// trip, so no separate lapsed-lease read-back is needed here.
    /// </summary>
    /// <returns>The freshly minted lease token on a won claim, or <see langword="null"/> if refused.</returns>
    private async Task<string?> TryClaimAsync(string id, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid().ToString();
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = Key(id),
                UpdateExpression = "SET leaseUntil = :leaseUntil, leaseToken = :leaseToken",
                ConditionExpression =
                    "attribute_exists(#pk) AND #status = :pending " +
                    "AND (attribute_not_exists(nextAttemptAtUtc) OR nextAttemptAtUtc <= :now) " +
                    "AND (attribute_not_exists(leaseUntil) OR leaseUntil < :now)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#pk"] = _partitionKeyAttribute,
                    ["#status"] = "status"
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":leaseUntil"] = new AttributeValue { S = DynamoDbOutboxItemMapper.FormatTimestamp(now + lease) },
                    [":leaseToken"] = new AttributeValue { S = leaseToken },
                    [":pending"] = new AttributeValue { S = StatusPending },
                    [":now"] = new AttributeValue { S = DynamoDbOutboxItemMapper.FormatTimestamp(now) }
                }
            }, cancellationToken);

            return leaseToken;
        }
        catch (ConditionalCheckFailedException)
        {
            // Another claimer holds a live lease, the envelope isn't due yet, isn't Pending, or
            // doesn't exist - refuse the claim (caller treats this id as "not claimed").
            return null;
        }
    }

    /// <summary>
    /// Issues a settle <c>UpdateItem</c> whose <see cref="UpdateItemRequest.ConditionExpression"/> is
    /// already scoped to <c>attribute_exists(#pk) AND leaseToken = :leaseToken</c> by the caller,
    /// translating a condition failure (envelope gone, or <c>leaseToken</c> no longer matches - claim
    /// fencing, see <see cref="IOutboxStore.MarkDispatchedAsync"/>'s remarks) into
    /// <see langword="false"/> rather than an exception.
    /// </summary>
    private async Task<bool> TryUpdateAsync(string id, UpdateItemRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(request, cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            // The envelope no longer exists, or leaseToken has moved on to another claimer - refuse
            // the write (a no-op) rather than clobbering whoever holds the lease now.
            return false;
        }
    }

    private Dictionary<string, AttributeValue> Key(string id)
        => new() { [_partitionKeyAttribute] = new AttributeValue { S = id } };
}
