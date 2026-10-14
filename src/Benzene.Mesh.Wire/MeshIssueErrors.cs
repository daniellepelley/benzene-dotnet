using System.Text.Json;
using System.Text.Json.Serialization;
using Benzene.Abstractions.Results;

namespace Benzene.Mesh.Wire;

/// <summary>
/// An issue's error detail (spec §4.1): the (<c>field</c>, <c>code</c>) pairs behind the failures it
/// counts, merged by identity and bounded the same way on both sides of the feed — the emitter at its
/// flush (<see cref="HttpMeshIssueExporter"/>), the collector when it merges a batch's deltas.
/// </summary>
/// <remarks>
/// An empty <c>field</c>/<c>code</c> string is the same as an absent member (spec §4.1), so an empty
/// member merges with an absent one, and an entry whose members are both empty has neither and is not
/// counted; an absent member is distinct from every non-empty string and sorts before it.
/// </remarks>
public static class MeshIssueErrors
{
    /// <summary>The most entries an issue carries (spec §4.1): the highest counts, ties broken by
    /// field then code (ordinal, absent first).</summary>
    public const int MaxEntries = 10;

    /// <summary>The most distinct pairs the emitter accumulates per issue per flush window. Bounding
    /// to <see cref="MaxEntries"/> happens only at the flush (so a pair that becomes frequent late in
    /// the window is not evicted early); pairs past this cap are not admitted, which bounds memory.</summary>
    public const int MaxPendingPairs = 100;

    /// <summary>A failing result's structured errors reduced to (field, code) pairs, one count each —
    /// <b>never the message</b>, which is free text a validator may have built from the payload. An
    /// error carrying neither member is not counted. Null when nothing is left.</summary>
    public static List<MeshIssueError>? FromResultErrors(IReadOnlyList<BenzeneError>? errors)
    {
        if (errors is not { Count: > 0 })
        {
            return null;
        }

        List<MeshIssueError>? pairs = null;
        foreach (var error in errors)
        {
            if (error == null)
            {
                continue;
            }
            var field = Normalize(error.Field);
            var code = Normalize(error.Code);
            if (field == null && code == null)
            {
                continue;
            }
            (pairs ??= new List<MeshIssueError>()).Add(new MeshIssueError { Field = field, Code = code, Count = 1 });
        }
        return pairs;
    }

    /// <summary>Adds <paramref name="delta"/> into <paramref name="existing"/> by (field, code)
    /// identity (<c>count += delta</c>) and returns the bounded result: the <see cref="MaxEntries"/>
    /// highest counts, ordered count descending, then field, then code (ordinal, absent first). An entry
    /// with neither member or a non-positive count is skipped. Null when nothing is held — so an issue
    /// without detail omits <c>errors</c>. Neither input is modified; the result is a fresh list.</summary>
    public static List<MeshIssueError>? Merge(IEnumerable<MeshIssueError>? existing, IEnumerable<MeshIssueError>? delta)
    {
        var counts = new Dictionary<(string? Field, string? Code), long>();
        Add(counts, existing);
        Add(counts, delta);
        return Bound(counts);
    }

    /// <summary>Bounds and orders accumulated (field, code) counts for the wire — see <see cref="Merge"/>.</summary>
    public static List<MeshIssueError>? Bound(IReadOnlyDictionary<(string? Field, string? Code), long> counts)
    {
        if (counts.Count == 0)
        {
            return null;
        }

        var entries = counts
            .Where(x => x.Value > 0)
            .Select(x => new MeshIssueError { Field = x.Key.Field, Code = x.Key.Code, Count = x.Value })
            .ToList();
        entries.Sort(Compare);
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }
        return entries.Count == 0 ? null : entries;
    }

    /// <summary>The spec §4.1 order: count descending, then field, then code, ordinal with absent
    /// (null) first.</summary>
    public static int Compare(MeshIssueError a, MeshIssueError b)
    {
        var byCount = b.Count.CompareTo(a.Count);
        if (byCount != 0)
        {
            return byCount;
        }
        // string.CompareOrdinal orders null before every string, which is the "absent first" rule.
        var byField = string.CompareOrdinal(a.Field, b.Field);
        return byField != 0 ? byField : string.CompareOrdinal(a.Code, b.Code);
    }

    internal static string? Normalize(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static void Add(Dictionary<(string? Field, string? Code), long> counts, IEnumerable<MeshIssueError>? entries)
    {
        if (entries == null)
        {
            return;
        }
        foreach (var entry in entries)
        {
            if (entry == null || entry.Count <= 0)
            {
                continue;
            }
            var key = (Normalize(entry.Field), Normalize(entry.Code));
            if (key.Item1 == null && key.Item2 == null)
            {
                continue;
            }
            counts[key] = counts.TryGetValue(key, out var count) ? count + entry.Count : entry.Count;
        }
    }
}

/// <summary>
/// Reads an issue's <c>errors</c> leniently (spec §4.1): an entry that is not an object, has neither
/// <c>field</c> nor <c>code</c> (as strings), or whose <c>count</c> is not a positive integer is
/// skipped; a value that is not an array at all reads as no detail. A malformed entry therefore never
/// fails the issue — or the batch — it arrived in. Writes the plain wire shape.
/// </summary>
public sealed class MeshIssueErrorsJsonConverter : JsonConverter<List<MeshIssueError>?>
{
    /// <inheritdoc />
    public override List<MeshIssueError>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // ParseValue consumes the whole value whatever its shape, leaving the reader where the
        // serializer expects it — so a non-array is skipped, not an error.
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<MeshIssueError>();
        foreach (var item in root.EnumerateArray())
        {
            if (TryReadEntry(item, out var entry))
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<MeshIssueError>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var entry in value)
        {
            if (entry == null)
            {
                continue;
            }
            writer.WriteStartObject();
            if (entry.Field != null)
            {
                writer.WriteString("field", entry.Field);
            }
            if (entry.Code != null)
            {
                writer.WriteString("code", entry.Code);
            }
            writer.WriteNumber("count", entry.Count);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static bool TryReadEntry(JsonElement item, out MeshIssueError entry)
    {
        entry = null!;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? field = null;
        string? code = null;
        long? count = null;
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, "field", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadMember(property.Value, out field)) return false;
            }
            else if (string.Equals(property.Name, "code", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadMember(property.Value, out code)) return false;
            }
            else if (string.Equals(property.Name, "count", StringComparison.OrdinalIgnoreCase))
            {
                // TryGetInt64 rejects a fraction or exponent form (1.5, 2.0, 1e2): "a positive integer".
                count = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var parsed)
                    ? parsed
                    : null;
            }
        }

        field = MeshIssueErrors.Normalize(field);
        code = MeshIssueErrors.Normalize(code);
        if (count is not > 0 || (field == null && code == null))
        {
            return false;
        }
        entry = new MeshIssueError { Field = field, Code = code, Count = count.Value };
        return true;
    }

    // A member is a string or absent; null reads as absent, any other kind makes the entry malformed.
    private static bool TryReadMember(JsonElement value, out string? member)
    {
        member = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                member = value.GetString();
                return true;
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }
}
