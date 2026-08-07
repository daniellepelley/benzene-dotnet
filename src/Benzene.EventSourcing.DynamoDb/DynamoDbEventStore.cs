using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Benzene.EventSourcing.DynamoDb;

/// <summary>
/// A distributed <see cref="IEventStore"/> backed by Amazon DynamoDB: one item per event, keyed
/// <c>(streamId, version)</c>. Appends are a single <c>TransactWriteItems</c> where each event's write
/// is conditional on that <c>(streamId, version)</c> not already existing — so two writers racing the
/// same expected version cannot both succeed, giving optimistic concurrency without a lock. The table's
/// DynamoDB stream is the projection feed (consume it with <c>Benzene.Aws.Lambda.DynamoDb</c>).
/// </summary>
/// <remarks>
/// The table needs a composite key: a string partition key (the stream id, default attribute <c>pk</c>)
/// and a numeric sort key (the version, default attribute <c>version</c>). The consumer registers
/// <see cref="IAmazonDynamoDB"/> and provisions the table; this package does neither.
/// </remarks>
public class DynamoDbEventStore : IEventStore
{
    // TransactWriteItems is atomic but bounded; an append larger than this must be split by the caller.
    private const int MaxEventsPerAppend = 100;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly string _partitionKey;
    private readonly string _sortKey;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="DynamoDbEventStore"/> class.</summary>
    /// <param name="dynamoDb">The DynamoDB client.</param>
    /// <param name="tableName">The event table (composite key: string partition + numeric sort).</param>
    /// <param name="partitionKeyAttribute">The stream-id partition-key attribute. Defaults to <c>pk</c>.</param>
    /// <param name="sortKeyAttribute">The version sort-key attribute. Defaults to <c>version</c>.</param>
    /// <param name="now">Clock, injectable for testing. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public DynamoDbEventStore(
        IAmazonDynamoDB dynamoDb,
        string tableName,
        string partitionKeyAttribute = "pk",
        string sortKeyAttribute = "version",
        Func<DateTimeOffset>? now = null)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _partitionKey = partitionKeyAttribute;
        _sortKey = sortKeyAttribute;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<long> AppendAsync(string streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return expectedVersion;
        }

        if (events.Count > MaxEventsPerAppend)
        {
            throw new ArgumentException(
                $"Cannot append {events.Count} events atomically; DynamoDB transactions are limited to {MaxEventsPerAppend} items. Split the append.",
                nameof(events));
        }

        var now = _now();
        var version = expectedVersion;
        var writes = new List<TransactWriteItem>(events.Count);
        foreach (var e in events)
        {
            version++;
            writes.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        [_partitionKey] = new AttributeValue { S = streamId },
                        [_sortKey] = new AttributeValue { N = version.ToString() },
                        ["eventType"] = new AttributeValue { S = e.EventType },
                        ["payload"] = new AttributeValue { S = e.Payload },
                        ["timestamp"] = new AttributeValue { S = now.ToString("O") }
                    },
                    // The (streamId, version) slot must be free: if another writer already took this
                    // version, the whole transaction is cancelled — that's the concurrency check.
                    ConditionExpression = "attribute_not_exists(#pk)",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = _partitionKey }
                }
            });
        }

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
            return version;
        }
        catch (TransactionCanceledException)
        {
            var actual = await CurrentVersionAsync(streamId, cancellationToken);
            throw new EventStoreConcurrencyException(streamId, expectedVersion, actual);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> ReadAsync(string streamId, long fromVersion = 0, CancellationToken cancellationToken = default)
    {
        var results = new List<StoredEvent>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "#pk = :pk AND #sk > :from",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = _partitionKey, ["#sk"] = _sortKey },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = streamId },
                    [":from"] = new AttributeValue { N = fromVersion.ToString() }
                },
                ScanIndexForward = true,
                ExclusiveStartKey = lastKey
            }, cancellationToken);

            foreach (var item in response.Items)
            {
                results.Add(ToStoredEvent(streamId, item));
            }

            lastKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (lastKey != null);

        return results;
    }

    private StoredEvent ToStoredEvent(string streamId, IReadOnlyDictionary<string, AttributeValue> item)
    {
        var version = long.Parse(item[_sortKey].N);
        var eventType = item.TryGetValue("eventType", out var t) ? t.S : string.Empty;
        var payload = item.TryGetValue("payload", out var p) ? p.S : string.Empty;
        var timestamp = item.TryGetValue("timestamp", out var ts) && DateTimeOffset.TryParse(ts.S, out var parsed)
            ? parsed
            : default;
        return new StoredEvent(streamId, version, eventType, payload, timestamp);
    }

    private async Task<long> CurrentVersionAsync(string streamId, CancellationToken cancellationToken)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = _partitionKey },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = new AttributeValue { S = streamId } },
            ScanIndexForward = false,
            Limit = 1
        }, cancellationToken);

        return response.Items.Count > 0 ? long.Parse(response.Items[0][_sortKey].N) : 0;
    }
}
