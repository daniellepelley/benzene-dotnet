namespace Benzene.Core.Messages;

/// <summary>
/// How a binding reads its reserved keys (the topic, by default <c>topic</c>) out of a transport's native
/// metadata - SQS/SNS message attributes, Service Bus/Event Hub properties, Pub/Sub attributes, RabbitMQ
/// headers (wire-contracts §2). Header keys are case-insensitive on read, so a lookup never goes through
/// the native dictionary's own comparer, which for most SDKs is ordinal: <c>Topic</c> routes exactly as
/// <c>topic</c> does. A key that appears more than once under different casings follows the header
/// dictionary's rule - the last value wins.
/// </summary>
public static class MetadataKeys
{
    /// <summary>Whether <paramref name="candidate"/> names <paramref name="key"/>, ignoring case.</summary>
    /// <param name="candidate">A key from the native metadata.</param>
    /// <param name="key">The reserved key being looked for.</param>
    public static bool Matches(string? candidate, string? key)
    {
        return string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds <paramref name="key"/> in <paramref name="metadata"/>, ignoring case.
    /// </summary>
    /// <typeparam name="TValue">The native metadata's value type.</typeparam>
    /// <param name="metadata">The native metadata; <c>null</c> finds nothing.</param>
    /// <param name="key">The key to find.</param>
    /// <param name="value">The value found, or <c>default</c>.</param>
    /// <returns><c>true</c> when the key is present under any casing.</returns>
    public static bool TryGetValue<TValue>(IEnumerable<KeyValuePair<string, TValue>>? metadata, string key, out TValue value)
    {
        value = default!;
        if (metadata == null)
        {
            return false;
        }

        var found = false;
        foreach (var pair in metadata)
        {
            if (Matches(pair.Key, key))
            {
                value = pair.Value;
                found = true;
            }
        }

        return found;
    }
}
