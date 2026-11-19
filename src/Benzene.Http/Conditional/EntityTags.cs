namespace Benzene.Http.Conditional;

/// <summary>
/// Entity-tag comparison for conditional requests (RFC 9110 §8.8.3, §13.1). An entity tag is a quoted
/// string, optionally prefixed <c>W/</c> (weak): <c>"v1"</c>, <c>W/"v1"</c>.
/// </summary>
public static class EntityTags
{
    /// <summary>Makes a strong entity tag from an opaque value: <c>Strong("v1")</c> gives <c>"v1"</c>.</summary>
    public static string Strong(string value)
    {
        return $"\"{value}\"";
    }

    /// <summary>Makes a weak entity tag from an opaque value: <c>Weak("v1")</c> gives <c>W/"v1"</c>.</summary>
    public static string Weak(string value)
    {
        return $"W/\"{value}\"";
    }

    /// <summary>
    /// Whether an <c>If-Match</c> header matches <paramref name="currentETag"/>: <c>*</c> matches any,
    /// and a list matches when one member does. Strong comparison (RFC 9110 §13.1.1): a weak tag never
    /// matches.
    /// </summary>
    public static bool IfMatch(string? ifMatch, string currentETag)
    {
        return Matches(ifMatch, currentETag, weak: false);
    }

    /// <summary>
    /// Whether an <c>If-None-Match</c> header matches <paramref name="currentETag"/>, meaning the
    /// caller's copy is current: <c>*</c> matches any, a list when one member does. Weak comparison
    /// (RFC 9110 §13.1.2): <c>W/"v1"</c> matches <c>"v1"</c>.
    /// </summary>
    public static bool IfNoneMatch(string? ifNoneMatch, string currentETag)
    {
        return Matches(ifNoneMatch, currentETag, weak: true);
    }

    private static bool Matches(string? header, string currentETag, bool weak)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(currentETag))
        {
            return false;
        }

        if (header.Trim() == "*")
        {
            return true;
        }

        var current = currentETag.Trim();
        foreach (var candidate in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (weak)
            {
                if (Opaque(candidate) == Opaque(current))
                {
                    return true;
                }
            }
            else if (!IsWeak(candidate) && !IsWeak(current) && candidate == current)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWeak(string tag)
    {
        return tag.StartsWith("W/", StringComparison.Ordinal);
    }

    private static string Opaque(string tag)
    {
        return IsWeak(tag) ? tag.Substring(2) : tag;
    }
}
