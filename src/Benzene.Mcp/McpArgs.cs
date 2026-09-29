using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Benzene.Mcp;

/// <summary>
/// Reading a tool call's arguments, strictly.
/// </summary>
/// <remarks>
/// A model writes these, so they arrive as whatever it decided to write: a date as "March 2026", a
/// number as a string, a missing field it thought was optional. Every one of those is refused here
/// with a sentence saying what was expected (as a <see cref="McpRequestException"/>, which becomes a
/// refused tool result), because a refusal the model can read and correct is worth far more than a
/// silent coercion that records the wrong date. The coercions that <em>are</em> made ("true" as a
/// string, "£12.00", a comma-separated list) are the ones where the request was perfectly clear.
/// </remarks>
public static class McpArgs
{
    /// <summary>A required string; whitespace counts as missing.</summary>
    public static string RequiredText(JsonObject args, string name) =>
        OptionalText(args, name) ?? throw new McpRequestException($"'{name}' is required.");

    /// <summary>An optional string, trimmed; a non-string value is returned as its JSON text.</summary>
    public static string? OptionalText(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null) return null;
        var value = node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Null => null,
            _ => node.ToJsonString(),
        };
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>A required id.</summary>
    public static Guid RequiredGuid(JsonObject args, string name)
    {
        var text = RequiredText(args, name);
        return Guid.TryParse(text, out var id)
            ? id
            : throw new McpRequestException($"'{name}' has to be an id, and '{text}' is not one.");
    }

    /// <summary>An optional id.</summary>
    public static Guid? OptionalGuid(JsonObject args, string name)
    {
        var text = OptionalText(args, name);
        if (text is null) return null;
        return Guid.TryParse(text, out var id)
            ? id
            : throw new McpRequestException($"'{name}' has to be an id, and '{text}' is not one.");
    }

    /// <summary>A date is <c>yyyy-MM-dd</c> and nothing else. "Last Tuesday" is the model's job to resolve, not ours.</summary>
    public static string? OptionalDate(JsonObject args, string name)
    {
        var text = OptionalText(args, name);
        if (text is null) return null;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new McpRequestException($"'{name}' has to be a date written yyyy-MM-dd, and '{text}' is not.");
        return text;
    }

    /// <summary>A required date, as <c>yyyy-MM-dd</c> text.</summary>
    public static string RequiredDate(JsonObject args, string name) =>
        OptionalDate(args, name) ?? throw new McpRequestException($"'{name}' is required, written yyyy-MM-dd.");

    /// <summary>The same, parsed, where leaving it out means something different from any date.</summary>
    public static DateOnly? OptionalDateOnly(JsonObject args, string name) =>
        OptionalDate(args, name) is { } text
            ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    /// <summary>The same date, parsed, for the handlers that take one already parsed rather than as text.</summary>
    public static DateOnly RequiredDateOnly(JsonObject args, string name) =>
        DateOnly.ParseExact(RequiredDate(args, name), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An optional whole number, written as a number or as digits in a string.</summary>
    public static int? OptionalInt(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null) return null;
        if (node.GetValueKind() == JsonValueKind.Number) return node.GetValue<int>();
        var text = OptionalText(args, name);
        if (text is null) return null;
        return int.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new McpRequestException($"'{name}' has to be a whole number, and '{text}' is not.");
    }

    /// <summary>A required whole number.</summary>
    public static int RequiredInt(JsonObject args, string name) =>
        OptionalInt(args, name) ?? throw new McpRequestException($"'{name}' is required, as a whole number.");

    /// <summary>A required amount.</summary>
    public static decimal RequiredDecimal(JsonObject args, string name) =>
        OptionalDecimal(args, name) ?? throw new McpRequestException($"'{name}' is required, as an amount.");

    /// <summary>An optional amount: a number, or a string like "12.00", "£12.00" or "1,234.00".</summary>
    public static decimal? OptionalDecimal(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null) return null;
        if (node.GetValueKind() == JsonValueKind.Number) return node.GetValue<decimal>();
        var text = OptionalText(args, name);
        if (text is null) return null;
        var cleaned = text.Replace("£", "").Replace("$", "").Replace("€", "").Replace(",", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new McpRequestException($"'{name}' has to be an amount, and '{text}' is not one.");
    }

    /// <summary>
    /// One of a fixed set, matched without regard to case. A value outside the set is refused with the
    /// set named: a schema's enum is a hint to a model rather than a rule it cannot break, so the list
    /// has to be enforced here as well as declared there.
    /// </summary>
    public static string? OptionalChoice(JsonObject args, string name, params string[] allowed)
    {
        var text = OptionalText(args, name);
        if (text is null) return null;
        var match = allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new McpRequestException(
            $"'{name}' has to be one of {string.Join(", ", allowed)}, and '{text}' is not.");
    }

    /// <summary>A required choice from a fixed set.</summary>
    public static string RequiredChoice(JsonObject args, string name, params string[] allowed) =>
        OptionalChoice(args, name, allowed)
        ?? throw new McpRequestException($"'{name}' is required: one of {string.Join(", ", allowed)}.");

    /// <summary>
    /// A true or false, however it was written. A model writes "true" as a string often enough that
    /// refusing it would be refusing a request that was perfectly clear.
    /// </summary>
    public static bool? OptionalFlag(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null) return null;
        var kind = node.GetValueKind();
        if (kind is JsonValueKind.True or JsonValueKind.False) return node.GetValue<bool>();
        if (kind == JsonValueKind.Null) return null;
        var text = OptionalText(args, name);
        if (text is null) return null;
        return bool.TryParse(text, out var value)
            ? value
            : throw new McpRequestException($"'{name}' has to be true or false, and '{text}' is not.");
    }

    /// <summary>
    /// A list of objects, for the tools that take several of the same thing. Refused item by item and
    /// by position, because "has to be a list of objects" leaves a model guessing which one it got
    /// wrong when four of the five were fine. A list written out as a JSON string is accepted too: a
    /// client holding an older copy of a tool's schema sends what it was given.
    /// </summary>
    public static IReadOnlyList<JsonObject> Objects(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null) return Array.Empty<JsonObject>();
        if (node is JsonValue text && text.GetValueKind() == JsonValueKind.String)
        {
            try { node = JsonNode.Parse(text.GetValue<string>()); }
            catch (JsonException) { throw new McpRequestException($"'{name}' has to be a list, and one was not given."); }
        }
        if (node is not JsonArray array)
            throw new McpRequestException($"'{name}' has to be a list, and one was not given.");

        var items = new List<JsonObject>();
        for (var i = 0; i < array.Count; i++)
            items.Add(array[i] as JsonObject
                ?? throw new McpRequestException($"Item {i + 1} of '{name}' has to be an object with its own fields."));
        return items;
    }

    /// <summary>
    /// A list of strings, however the model chose to write it: a JSON array, or one string with commas
    /// in it. Both arrive in practice.
    /// </summary>
    public static IReadOnlyList<string> OptionalList(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null) return Array.Empty<string>();
        if (node is JsonArray array)
            return array.Where(n => n is not null)
                .Select(n => n!.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString())
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        var text = OptionalText(args, name);
        if (text is null) return Array.Empty<string>();
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
