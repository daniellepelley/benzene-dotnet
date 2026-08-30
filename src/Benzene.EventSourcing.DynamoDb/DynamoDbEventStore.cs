using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    // TransactWriteItems is atomic but bounded at 100 items; an append larger than this must be split
    // by the caller. When expectedVersion > 0 the transaction also carries an extra ConditionCheck
    // item (the optimistic-concurrency check on the existing stream, see AppendAsync below) on top of
    // one Put per event, so the effective per-call cap for an append onto an EXISTING stream is
    // MaxEventsPerAppend - 1, not MaxEventsPerAppend (#271).
    private const int MaxEventsPerAppend = 100;

    // Attribute names this store writes onto every event item; a key attribute colliding with one of
    // these would silently corrupt writes (the key value would be clobbered by the event data, or
    // vice versa), so the constructor rejects it up front.
    private static readonly HashSet<string> ReservedAttributeNames = new(StringComparer.Ordinal) { "eventType", "payload", "timestamp" };

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
        if (dynamoDb is null)
        {
            throw new ArgumentNullException(nameof(dynamoDb));
        }

        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new ArgumentException("Table name must not be null or empty.", nameof(tableName));
        }

        if (string.IsNullOrWhiteSpace(partitionKeyAttribute))
        {
            throw new ArgumentException("Partition key attribute must not be null or empty.", nameof(partitionKeyAttribute));
        }

        if (string.IsNullOrWhiteSpace(sortKeyAttribute))
        {
            throw new ArgumentException("Sort key attribute must not be null or empty.", nameof(sortKeyAttribute));
        }

        if (partitionKeyAttribute == sortKeyAttribute)
        {
            throw new ArgumentException(
                $"Partition key attribute and sort key attribute must be different (both were '{partitionKeyAttribute}').",
                nameof(sortKeyAttribute));
        }

        if (ReservedAttributeNames.Contains(partitionKeyAttribute))
        {
            throw new ArgumentException(
                $"Partition key attribute '{partitionKeyAttribute}' collides with a reserved event attribute name (one of: {string.Join(", ", ReservedAttributeNames)}).",
                nameof(partitionKeyAttribute));
        }

        if (ReservedAttributeNames.Contains(sortKeyAttribute))
        {
            throw new ArgumentException(
                $"Sort key attribute '{sortKeyAttribute}' collides with a reserved event attribute name (one of: {string.Join(", ", ReservedAttributeNames)}).",
                nameof(sortKeyAttribute));
        }

        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _partitionKey = partitionKeyAttribute;
        _sortKey = sortKeyAttribute;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<long> AppendAsync(string streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken = default)
    {
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), expectedVersion, "Expected version cannot be negative.");
        }

        // Appending to an existing stream (expectedVersion > 0) reserves one of the 100 transact-write
        // items for the version ConditionCheck below, so the effective cap on event count is one less
        // than for a brand-new stream (expectedVersion == 0, which has no ConditionCheck item).
        var effectiveMaxEventsPerAppend = expectedVersion > 0 ? MaxEventsPerAppend - 1 : MaxEventsPerAppend;
        if (events.Count > effectiveMaxEventsPerAppend)
        {
            var message = expectedVersion > 0
                ? $"Cannot append {events.Count} events atomically at expectedVersion {expectedVersion}; appending to an existing stream reserves one of DynamoDB's {MaxEventsPerAppend}-item transact-write limit for the optimistic-concurrency check, so the effective limit is {effectiveMaxEventsPerAppend} events. Split the append."
                : $"Cannot append {events.Count} events atomically; DynamoDB transactions are limited to {MaxEventsPerAppend} items. Split the append.";
            throw new ArgumentException(message, nameof(events));
        }

        if (events.Count == 0)
        {
            // An empty batch has no Put items to hang a condition off, so the concurrency check has
            // to be a direct read instead — otherwise an empty append would silently accept any
            // expectedVersion, diverging from InMemoryEventStore (which always checks, even for an
            // empty batch).
            var actualVersion = await CurrentVersionAsync(streamId, cancellationToken);
            if (actualVersion != expectedVersion)
            {
                throw new EventStoreConcurrencyException(streamId, expectedVersion, actualVersion);
            }

            return expectedVersion;
        }

        var now = _now();
        var version = expectedVersion;
        var writes = new List<TransactWriteItem>(events.Count + 1);

        if (expectedVersion > 0)
        {
            // Verify the stream is actually AT expectedVersion, not merely that the Put slots below
            // are free: without this, an expectedVersion ahead of the real head would find its target
            // slots free (nothing has written them yet), the transaction would succeed, and the
            // stream would be permanently gapped for any correct writer that folds it from the start.
            writes.Add(new TransactWriteItem
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [_partitionKey] = new AttributeValue { S = streamId },
                        [_sortKey] = new AttributeValue { N = expectedVersion.ToString() }
                    },
                    ConditionExpression = "attribute_exists(#pk)",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = _partitionKey }
                }
            });
        }

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
            await _dynamoDb.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems = writes,
                    // Deterministic from the append's own content: a retried request (client timeout,
                    // network blip) for the exact same append reuses the same token so DynamoDB
                    // treats it as the same idempotent attempt rather than a fresh conflicting one.
                    ClientRequestToken = BuildClientRequestToken(streamId, expectedVersion, events)
                },
                cancellationToken);
            return version;
        }
        catch (TransactionCanceledException ex)
        {
            if (!IsConcurrencyConflict(ex))
            {
                // Throttling, capacity, or validation failures are not concurrency conflicts —
                // rethrow so the caller sees (and can react to) the real failure.
                throw;
            }

            var actual = await SafeCurrentVersionAsync(streamId);
            throw new EventStoreConcurrencyException(streamId, expectedVersion, actual, ex);
        }
    }

    private static bool IsConcurrencyConflict(TransactionCanceledException ex) =>
        ex.CancellationReasons?.Any(r => r.Code is "ConditionalCheckFailed" or "TransactionConflict") ?? false;

    private async Task<long> SafeCurrentVersionAsync(string streamId)
    {
        // This is a diagnostic-only read-back after a confirmed conflict: it must run under its own
        // cancellation (not the caller's token — a caller racing another writer and then cancelling
        // should still see the conflict, not an unrelated OperationCanceledException), and a failure
        // here (e.g. throttling) must fall back to "unknown" rather than replacing the real conflict
        // exception with this one.
        try
        {
            return await CurrentVersionAsync(streamId, CancellationToken.None);
        }
        catch
        {
            return -1;
        }
    }

    private static string BuildClientRequestToken(string streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events)
    {
        var content = new StringBuilder();
        content.Append(streamId).Append('\u001F').Append(expectedVersion);
        foreach (var e in events)
        {
            content.Append('\u001F').Append(e.EventType).Append('\u001F').Append(e.Payload);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()));
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes).ToString();
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
                ExclusiveStartKey = lastKey,
                // A Query defaults to eventually consistent. The documented command-handler cycle is
                // rehydrate (ReadAsync) -> decide -> AppendAsync with the version just read; an
                // eventually-consistent read that misses the most recently committed event would let
                // the handler decide against stale state - the same read-your-writes discipline
                // DynamoDbIdempotencyStore/DynamoDbOutboxStore already apply to their own
                // correctness-critical reads (both request ConsistentRead explicitly).
                ConsistentRead = true
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
        var eventType = RequireStringAttribute(item, "eventType", streamId, version);
        var payload = RequireStringAttribute(item, "payload", streamId, version);
        var timestamp = item.TryGetValue("timestamp", out var ts) && DateTimeOffset.TryParse(ts.S, out var parsed)
            ? parsed
            : default;
        return new StoredEvent(streamId, version, eventType, payload, timestamp);
    }

    // A missing or wrong-type "eventType"/"payload" attribute means the item was never written by
    // this store (or the table is shared with something else) — defaulting to string.Empty would
    // silently hand the caller a fabricated event instead of surfacing the corruption.
    private static string RequireStringAttribute(IReadOnlyDictionary<string, AttributeValue> item, string attributeName, string streamId, long version)
    {
        if (!item.TryGetValue(attributeName, out var value) || value.S is null)
        {
            throw new InvalidOperationException(
                $"Stream '{streamId}' version {version}: attribute '{attributeName}' is missing or not a string (S) type.");
        }

        return value.S;
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
            Limit = 1,
            // Same reasoning as ReadAsync: a caller may retry its append using ActualVersion, so an
            // eventually-consistent read here could hand back a stale "actual" version.
            ConsistentRead = true
        }, cancellationToken);

        return response.Items.Count > 0 ? long.Parse(response.Items[0][_sortKey].N) : 0;
    }
}
