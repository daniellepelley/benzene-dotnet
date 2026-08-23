namespace Benzene.Aws.Lambda.Sns;

/// <summary>
/// Provides helper methods for reading SNS message attributes.
/// </summary>
public static class SnsUtils
{
    /// <summary>
    /// Gets a message attribute value from an SNS record by key.
    /// </summary>
    /// <param name="context">The SNS record context to read the attribute from.</param>
    /// <param name="key">The message attribute key to look up.</param>
    /// <returns>The attribute value, or null if the attribute isn't present.</returns>
    public static string? GetFromAttributes(SnsRecordContext context, string key)
    {
        var attributes = context.SnsRecord.Sns?.MessageAttributes;

        return attributes != null && attributes.TryGetValue(key, out var value) ? value.Value : null;
    }
}
