using System.Globalization;
using Amazon.DynamoDBv2.Model;

namespace Benzene.Outbox.DynamoDb;

/// <summary>
/// Maps an <see cref="OutboxEnvelope"/> to and from the DynamoDB item shape shared by
/// <see cref="DynamoDbOutboxStore"/> and <see cref="DynamoDbOutboxTransaction"/>, so both write the
/// exact same attribute layout (see the table shape documented in this package's <c>CLAUDE.md</c>).
/// </summary>
internal static class DynamoDbOutboxItemMapper
{
    /// <summary>The sparse GSI's constant partition-key value, present only while an envelope is <see cref="OutboxStatus.Pending"/>.</summary>
    internal const string GsiPendingValue = "pending";

    /// <summary>The sparse GSI's partition-key attribute name.</summary>
    internal const string GsiPartitionKeyAttribute = "gsiPk";

    /// <summary>The sparse GSI's sort-key attribute name (mirrors <c>nextAttemptAtUtc</c>, or <c>createdAtUtc</c> when an envelope has never been attempted).</summary>
    internal const string GsiSortKeyAttribute = "gsiSk";

    /// <summary>
    /// Builds the full item (business attributes + the sparse-GSI attributes when the envelope is
    /// <see cref="OutboxStatus.Pending"/>) for a freshly captured envelope. Used by both
    /// <see cref="DynamoDbOutboxStore.AddAsync"/> and <see cref="DynamoDbOutboxTransaction.CommitAsync"/> -
    /// every envelope this package ever writes starts life through this same mapping.
    /// </summary>
    /// <param name="envelope">The envelope to map.</param>
    /// <param name="partitionKeyAttribute">The table's partition-key attribute name.</param>
    public static Dictionary<string, AttributeValue> ToItem(OutboxEnvelope envelope, string partitionKeyAttribute)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            [partitionKeyAttribute] = new AttributeValue { S = envelope.Id },
            ["topic"] = new AttributeValue { S = envelope.Topic },
            ["payload"] = new AttributeValue { S = envelope.Payload },
            ["payloadType"] = new AttributeValue { S = envelope.PayloadType },
            ["headers"] = new AttributeValue
            {
                M = envelope.Headers.ToDictionary(kv => kv.Key, kv => new AttributeValue { S = kv.Value })
            },
            ["createdAtUtc"] = new AttributeValue { S = FormatTimestamp(envelope.CreatedAtUtc) },
            ["attemptCount"] = new AttributeValue { N = envelope.AttemptCount.ToString(CultureInfo.InvariantCulture) },
            ["status"] = new AttributeValue { S = envelope.Status.ToString() }
        };

        if (envelope.NextAttemptAtUtc.HasValue)
        {
            item["nextAttemptAtUtc"] = new AttributeValue { S = FormatTimestamp(envelope.NextAttemptAtUtc.Value) };
        }

        if (envelope.LastError != null)
        {
            item["lastError"] = new AttributeValue { S = envelope.LastError };
        }

        // Sparse GSI: present only while Pending, so the sweeper's index stays small (see CLAUDE.md).
        if (envelope.Status == OutboxStatus.Pending)
        {
            item[GsiPartitionKeyAttribute] = new AttributeValue { S = GsiPendingValue };
            item[GsiSortKeyAttribute] = new AttributeValue
            {
                S = FormatTimestamp(envelope.NextAttemptAtUtc ?? envelope.CreatedAtUtc)
            };
        }

        return item;
    }

    /// <summary>Rebuilds an <see cref="OutboxEnvelope"/> from a stored item (a query/get result).</summary>
    /// <param name="item">The item's attributes.</param>
    /// <param name="partitionKeyAttribute">The table's partition-key attribute name.</param>
    public static OutboxEnvelope ToEnvelope(IReadOnlyDictionary<string, AttributeValue> item, string partitionKeyAttribute)
    {
        var id = item[partitionKeyAttribute].S;
        var topic = item.TryGetValue("topic", out var topicValue) ? topicValue.S : string.Empty;
        var payload = item.TryGetValue("payload", out var payloadValue) ? payloadValue.S : string.Empty;
        var payloadType = item.TryGetValue("payloadType", out var payloadTypeValue) ? payloadTypeValue.S : string.Empty;
        var headers = item.TryGetValue("headers", out var headersValue) && headersValue.M != null
            ? headersValue.M.ToDictionary(kv => kv.Key, kv => kv.Value.S)
            : new Dictionary<string, string>();
        var createdAtUtc = item.TryGetValue("createdAtUtc", out var createdAtUtcValue)
            ? ParseTimestamp(createdAtUtcValue.S)
            : default;
        var attemptCount = item.TryGetValue("attemptCount", out var attemptCountValue) && int.TryParse(attemptCountValue.N, out var parsedAttemptCount)
            ? parsedAttemptCount
            : 0;
        var nextAttemptAtUtc = item.TryGetValue("nextAttemptAtUtc", out var nextAttemptValue)
            ? ParseTimestamp(nextAttemptValue.S)
            : (DateTimeOffset?)null;
        var status = item.TryGetValue("status", out var statusValue) && Enum.TryParse<OutboxStatus>(statusValue.S, out var parsedStatus)
            ? parsedStatus
            : OutboxStatus.Pending;
        var lastError = item.TryGetValue("lastError", out var lastErrorValue) ? lastErrorValue.S : null;

        return new OutboxEnvelope(id, topic, payload, payloadType, headers, createdAtUtc, attemptCount, nextAttemptAtUtc, status, lastError);
    }

    /// <summary>
    /// Formats a timestamp for storage: UTC, round-trip ("O"/ISO-8601) format. Fixed-width and always
    /// UTC, so lexical string ordering matches chronological ordering - required for <c>gsiSk</c> to
    /// sort correctly and safe for any other stored timestamp attribute.
    /// </summary>
    internal static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Parses a timestamp written by <see cref="FormatTimestamp"/>.</summary>
    internal static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
