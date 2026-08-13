using System.Text.Json.Serialization;

namespace Benzene.Outbox.DynamoDb;

/// <summary>
/// The deserialization target for a DynamoDB Streams record's image of an outbox item, as
/// unmarshalled to plain JSON by <c>Benzene.Aws.Lambda.DynamoDb</c>'s
/// <c>DynamoDbMessageBodyGetter</c> (AttributeValue JSON such as <c>{"id":{"S":"..."}}</c> becomes
/// plain JSON such as <c>{"id":"..."}</c>). This package owns the item schema (see
/// <see cref="DynamoDbOutboxStore"/>'s table-shape remarks), so the mapping from a stream image back
/// to something dispatch-relevant lives here - keeping the Lambda relay handler itself to a few
/// lines with no new package dependency of its own.
/// </summary>
/// <remarks>
/// Deliberately carries only the envelope's business/lifecycle attributes, not the store's
/// internal claim-plumbing ones (<c>leaseUntil</c>, <c>gsiPk</c>/<c>gsiSk</c>, <c>expiresAt</c>) -
/// a stream consumer has no legitimate use for those, and coupling it to them would leak
/// implementation detail across the package boundary. In practice, the relay handler built from
/// this type only needs <see cref="Id"/> (to call <c>IOutboxDispatcher.DispatchOneAsync</c>); the
/// rest is here for completeness and so this type can be used to reconstruct a full
/// <see cref="OutboxEnvelope"/> via <see cref="ToEnvelope"/> where useful (e.g. logging/diagnostics
/// without a round trip back to the table).
/// </remarks>
public sealed class OutboxStreamImage
{
    /// <summary>The envelope's id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The topic the envelope will eventually be sent on.</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    /// <summary>The request, serialized by the capturing route scope's <c>ISerializer</c>.</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;

    /// <summary>The assembly-qualified name of the request's runtime type.</summary>
    [JsonPropertyName("payloadType")]
    public string PayloadType { get; set; } = string.Empty;

    /// <summary>The post-stamping header snapshot captured at capture time.</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>When the envelope was captured.</summary>
    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>How many dispatch attempts have been made so far.</summary>
    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; set; }

    /// <summary>When the envelope next becomes due for a claim, or <see langword="null"/>.</summary>
    [JsonPropertyName("nextAttemptAtUtc")]
    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    /// <summary>The envelope's lifecycle state (<c>"Pending"</c>/<c>"Dispatched"</c>/<c>"Parked"</c>).</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>A description of the most recent dispatch failure, or <see langword="null"/>.</summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    /// <summary>
    /// Rebuilds the full <see cref="OutboxEnvelope"/> from this image. <see cref="Status"/> that
    /// doesn't parse as a known <see cref="OutboxStatus"/> falls back to <see cref="OutboxStatus.Pending"/>
    /// (a stream image is always for a just-inserted envelope in practice, which is always Pending).
    /// </summary>
    public OutboxEnvelope ToEnvelope()
    {
        var status = Enum.TryParse<OutboxStatus>(Status, out var parsed) ? parsed : OutboxStatus.Pending;
        return new OutboxEnvelope(Id, Topic, Payload, PayloadType, Headers, CreatedAtUtc, AttemptCount, NextAttemptAtUtc, status, LastError);
    }
}
