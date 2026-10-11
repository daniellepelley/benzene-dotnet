using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Benzene.Schema.OpenApi.EventService;

// Kept in this namespace (the type moved here from Benzene.CodeGen.Core, which type-forwards it) so
// existing `using Benzene.CodeGen.Core;` call sites keep compiling.
namespace Benzene.CodeGen.Core;

/// <summary>
/// The contract document's two hashes (contract-document.md §6):
/// <c>contractHash = hash({requests, events})</c> - what a client of the service depends on - and
/// <c>registrationHash = hash({requests, events, sends})</c> - what a mesh collector's graph depends
/// on - where <c>hash(P) = "sha256:" + lowercase-hex(sha256(JCS(P)))</c> and JCS is RFC 8785
/// (<see cref="JsonCanonicalizer"/>).
/// </summary>
/// <remarks>
/// <para>
/// The projection P (§6.2) is built from the document's JSON, so it is the same computation whether
/// the input was derived here, deserialized from a committed <c>.spec.json</c>, or served by another
/// port: (1) only <c>requests</c>, <c>events</c> and, for the registration hash, <c>sends</c>;
/// (2) <c>benzene:</c> entries dropped and each entry reduced to its allow-listed members; (3) every
/// <c>$ref</c> dereferenced, a reference already being expanded on the current path cut to <c>{}</c>;
/// (4) documentation keywords, <c>format</c>, <c>x-*</c> and <c>discriminator.mapping</c> removed,
/// <c>required</c> sorted, and the nullable wrapper around a cut reference reduced to <c>{}</c>;
/// (5) entries sorted by (topic, version) and <c>httpMappings</c> upper-cased and sorted. Every sort
/// compares UTF-16 code units, as JCS does.
/// </para>
/// <para>
/// Because P dereferences, component names, hoisting depth, entry order and schema documentation
/// never reach either hash, so two ports that derive the same wire shapes produce the same two
/// values - the property <c>conformance/contract-hash-cases.json</c> pins.
/// </para>
/// <para>
/// Both hashes are carried in every document this package emits (<see cref="EventServiceDocument"/>),
/// and a health check or drift check reads the <b>served</b> value rather than computing a private
/// one of its own.
/// </para>
/// </remarks>
public static class ContractHash
{
    /// <summary>The prefix every hash carries.</summary>
    public const string Prefix = "sha256:";

    private const string RefPrefix = "#/components/schemas/";

    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "title", "description", "default", "examples", "example", "deprecated", "readOnly", "writeOnly",
        "$comment", "$id", "$schema", "format",
    };

    private static readonly string[] RequestMembers = { "topic", "version", "httpMappings", "request", "response" };
    private static readonly string[] EventMembers = { "topic", "version", "message" };
    private static readonly string[] SendMembers = { "topic", "version", "request", "response" };
    private static readonly HashSet<string> SchemaMembers = new(StringComparer.Ordinal) { "request", "response", "message" };

    /// <summary>The <c>contractHash</c> of <paramref name="document"/> (§6.1).</summary>
    /// <param name="document">The document.</param>
    /// <returns><c>sha256:</c> and 64 lowercase hex digits.</returns>
    public static string Compute(EventServiceDocument document) => Compute(document.ToJsonObject(includeHashes: false));

    /// <summary>The <c>registrationHash</c> of <paramref name="document"/> (§6.1).</summary>
    /// <param name="document">The document.</param>
    /// <returns><c>sha256:</c> and 64 lowercase hex digits.</returns>
    public static string ComputeRegistrationHash(EventServiceDocument document)
        => ComputeRegistrationHash(document.ToJsonObject(includeHashes: false));

    /// <summary>The <c>contractHash</c> of a contract document given as JSON.</summary>
    /// <param name="documentJson">The document's JSON.</param>
    /// <returns><c>sha256:</c> and 64 lowercase hex digits.</returns>
    public static string Compute(string documentJson) => Compute(Parse(documentJson));

    /// <summary>The <c>registrationHash</c> of a contract document given as JSON.</summary>
    /// <param name="documentJson">The document's JSON.</param>
    /// <returns><c>sha256:</c> and 64 lowercase hex digits.</returns>
    public static string ComputeRegistrationHash(string documentJson) => ComputeRegistrationHash(Parse(documentJson));

    /// <summary>The <c>contractHash</c> of a contract document's JSON object.</summary>
    /// <param name="document">The document.</param>
    /// <returns><c>sha256:</c> and 64 lowercase hex digits.</returns>
    public static string Compute(JsonObject document) => Hash(Project(document, includeSends: false));

    /// <summary>The <c>registrationHash</c> of a contract document's JSON object.</summary>
    /// <param name="document">The document.</param>
    /// <returns><c>sha256:</c> and 64 lowercase hex digits.</returns>
    public static string ComputeRegistrationHash(JsonObject document) => Hash(Project(document, includeSends: true));

    /// <summary>
    /// The projection P of §6.2: <c>{requests, events}</c>, plus <c>sends</c> when
    /// <paramref name="includeSends"/>, each entry reduced, dereferenced, normalized and sorted.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="includeSends">True for the registration projection.</param>
    /// <returns>A new object; <paramref name="document"/> is not modified.</returns>
    public static JsonObject Project(JsonObject document, bool includeSends)
    {
        var components = (document["components"] as JsonObject)?["schemas"] as JsonObject;
        var projection = new JsonObject
        {
            ["requests"] = ProjectEntries(document["requests"], RequestMembers, components),
            ["events"] = ProjectEntries(document["events"], EventMembers, components),
        };

        if (includeSends)
        {
            projection["sends"] = ProjectEntries(document["sends"], SendMembers, components);
        }

        return projection;
    }

    /// <summary>
    /// A schema in its comparison form - §6.2 steps 3-4: dereferenced against
    /// <paramref name="components"/> (cycles cut to <c>{}</c>), annotations removed, <c>required</c>
    /// sorted. This is the form <c>derivationCases</c> compare a derived schema in.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <param name="components">The document's <c>components.schemas</c>, or null.</param>
    /// <returns>A new node.</returns>
    public static JsonNode? NormalizeSchema(JsonNode? schema, JsonObject? components)
        => Normalize(schema, components, ImmutableStack.Empty);

    private static JsonObject Parse(string json)
        => JsonNode.Parse(json) as JsonObject
           ?? throw new ArgumentException("A contract document must be a JSON object.", nameof(json));

    private static string Hash(JsonObject projection)
    {
        var canonical = JsonCanonicalizer.Canonicalize(projection);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static JsonArray ProjectEntries(JsonNode? entries, string[] members, JsonObject? components)
    {
        var projected = new List<JsonObject>();
        if (entries is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var topic = entry["topic"] is JsonValue topicValue && topicValue.TryGetValue<string>(out var t) ? t : null;
                if (topic != null && topic.StartsWith(Benzene.Abstractions.BenzeneTopic.Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var reduced = new JsonObject();
                foreach (var member in members)
                {
                    if (!entry.TryGetPropertyValue(member, out var value))
                    {
                        continue;
                    }

                    reduced[member] = member switch
                    {
                        _ when SchemaMembers.Contains(member) => NormalizeSchema(value, components),
                        "httpMappings" => NormalizeHttpMappings(value),
                        _ => value?.DeepClone(),
                    };
                }

                projected.Add(reduced);
            }
        }

        projected.Sort(CompareEntries);
        return new JsonArray(projected.Cast<JsonNode?>().ToArray());
    }

    private static int CompareEntries(JsonObject left, JsonObject right)
    {
        var byTopic = string.CompareOrdinal(StringOf(left["topic"]), StringOf(right["topic"]));
        if (byTopic != 0)
        {
            return byTopic;
        }

        var leftHasVersion = left.ContainsKey("version");
        var rightHasVersion = right.ContainsKey("version");
        if (leftHasVersion != rightHasVersion)
        {
            return leftHasVersion ? 1 : -1;
        }

        return string.CompareOrdinal(StringOf(left["version"]), StringOf(right["version"]));
    }

    private static JsonNode? NormalizeHttpMappings(JsonNode? value)
    {
        if (value is not JsonArray mappings)
        {
            return value?.DeepClone();
        }

        var normalized = mappings
            .Select(mapping => new
            {
                Method = StringOf(mapping?["method"]).ToUpperInvariant(),
                Path = StringOf(mapping?["path"]),
            })
            .OrderBy(x => x.Method, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => (JsonNode?)new JsonObject { ["method"] = x.Method, ["path"] = x.Path })
            .ToArray();

        return new JsonArray(normalized);
    }

    private static string StringOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : string.Empty;

    private static JsonNode? Normalize(JsonNode? schema, JsonObject? components, ImmutableStack path)
    {
        if (schema is not JsonObject obj)
        {
            return schema?.DeepClone();
        }

        // Step 3: a Reference Object is replaced by its target; its siblings are ignored.
        if (obj.TryGetPropertyValue("$ref", out var refNode))
        {
            var reference = StringOf(refNode);
            var name = reference.StartsWith(RefPrefix, StringComparison.Ordinal) ? reference.Substring(RefPrefix.Length) : reference;
            if (path.Contains(name))
            {
                return new JsonObject();
            }

            var target = components != null && components.TryGetPropertyValue(name, out var found) ? found : new JsonObject();
            return Normalize(target, components, path.Push(name));
        }

        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (Annotations.Contains(key) || key.StartsWith("x-", StringComparison.Ordinal))
            {
                continue;
            }

            switch (key)
            {
                case "properties" when value is JsonObject properties:
                    var normalizedProperties = new JsonObject();
                    foreach (var (propertyName, propertySchema) in properties)
                    {
                        normalizedProperties[propertyName] = Normalize(propertySchema, components, path);
                    }

                    result[key] = normalizedProperties;
                    break;
                case "items" or "additionalProperties" or "not" when value is JsonObject:
                    result[key] = Normalize(value, components, path);
                    break;
                case "allOf" or "anyOf" or "oneOf" when value is JsonArray members:
                    result[key] = new JsonArray(members.Select(member => Normalize(member, components, path)).ToArray());
                    break;
                case "discriminator" when value is JsonObject discriminator:
                    var withoutMapping = new JsonObject();
                    foreach (var (discriminatorKey, discriminatorValue) in discriminator)
                    {
                        if (discriminatorKey != "mapping")
                        {
                            withoutMapping[discriminatorKey] = discriminatorValue?.DeepClone();
                        }
                    }

                    result[key] = withoutMapping;
                    break;
                case "required" when value is JsonArray required:
                    if (required.Count > 0)
                    {
                        result[key] = new JsonArray(required
                            .Select(StringOf)
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .Select(x => (JsonNode?)JsonValue.Create(x))
                            .ToArray());
                    }

                    break;
                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }

        if (result["properties"] is JsonObject { Count: 0 })
        {
            result.Remove("properties");
        }

        // Step 4's last rule: the nullable wrapper (§4.2) around a reference cut at a cycle is {}.
        if (IsCutNullableWrapper(obj, result, path))
        {
            return new JsonObject();
        }

        return result;
    }

    private static bool IsCutNullableWrapper(JsonObject original, JsonObject normalized, ImmutableStack path)
    {
        if (normalized.Any(member => member.Key is not ("type" or "nullable" or "allOf")))
        {
            return false;
        }

        if (normalized["nullable"] is not JsonValue nullable || !nullable.TryGetValue<bool>(out var isNullable) || !isNullable)
        {
            return false;
        }

        if (normalized["allOf"] is not JsonArray { Count: 1 } normalizedMembers || normalizedMembers[0] is not JsonObject { Count: 0 })
        {
            return false;
        }

        // "an allOf of one member that was a cut reference": the original member was a $ref whose
        // target is already being expanded on this path.
        if (original["allOf"] is not JsonArray { Count: 1 } originalMembers
            || originalMembers[0] is not JsonObject originalMember
            || !originalMember.TryGetPropertyValue("$ref", out var refNode))
        {
            return false;
        }

        var reference = StringOf(refNode);
        var name = reference.StartsWith(RefPrefix, StringComparison.Ordinal) ? reference.Substring(RefPrefix.Length) : reference;
        return path.Contains(name);
    }

    /// <summary>The names being expanded on the current dereference path.</summary>
    private sealed class ImmutableStack
    {
        public static readonly ImmutableStack Empty = new(null, null);

        private readonly string? _head;
        private readonly ImmutableStack? _tail;

        private ImmutableStack(string? head, ImmutableStack? tail)
        {
            _head = head;
            _tail = tail;
        }

        public ImmutableStack Push(string name) => new(name, this);

        public bool Contains(string name)
        {
            for (var node = this; node is { _head: not null }; node = node._tail)
            {
                if (string.Equals(node._head, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
