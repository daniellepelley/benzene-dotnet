using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;

namespace Benzene.CodeGen.Core;

/// <summary>
/// Computes the spec-pinned <c>contractHash</c> (<c>docs/specification/contract-document.md</c> §6):
/// <c>"sha256:" + lowercase-hex(sha256(canonicalJSON(normalize(document))))</c>, with
/// <c>canonicalJSON</c> being RFC 8785 (<see cref="JsonCanonicalizer"/>). This is new, portable code
/// living beside (not replacing) <see cref="CodeGenHelpers.GenerateHash(EventServiceDocument)"/>,
/// which stays exactly as it was: it is a distinct algorithm (HMAC-SHA256 over the Microsoft.OpenApi
/// serializer's own JSON) that <c>Benzene.Mesh.Contracts.MeshHashing</c> deliberately mirrors for a
/// different, .NET-internal purpose (mesh.md §9) and must not change underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this can start from the Microsoft.OpenApi-serialized JSON at all</b> (the same JSON
/// <see cref="CodeGenHelpers.GenerateHash(EventServiceDocument)"/> hashes verbatim, which is exactly
/// why that byte stream isn't portable): <see cref="JsonCanonicalizer"/> re-sorts every object's
/// members and reformats every number independently of whatever order/whitespace/formatting the
/// source serializer chose, so the .NET-serializer-specific artifacts that made the old hash
/// non-portable are erased by canonicalization before hashing - only the JSON *value* the document
/// represents survives into the hash, which is exactly what a cross-language hash needs.
/// </para>
/// <para>
/// <b>The <paramref name="isTopicScoped"/>/<c>topicScoped</c> parameter on the methods below</b>
/// selects which of §6.2's two <c>normalize()</c> behaviours applies to a document's <c>requests[]</c>
/// reserved entries: <c>false</c> (the default - whole-service or a service-level client filtered by
/// an include-list, §5.2) strips reserved entries entirely, matching the domain-only ruling;
/// <c>true</c> (the topic-scoped, single-topic client shape of §5.3, as built by
/// <c>Benzene.CodeGen.Client.AtomicClientSdkBuilder</c>) does not - an explicitly-requested reserved
/// topic that is the *entire* document survives with only its <c>reserved</c> flag stripped, per
/// §6.2's own worked example. Every surviving <c>requests[]</c>/<c>events[]</c> entry's <c>example</c>
/// field, and the top-level <c>messageEndpoint</c>/<c>transports</c> fields, are always stripped
/// regardless of this parameter.
/// </para>
/// </remarks>
public static class ContractHash
{
    private const string Prefix = "sha256:";

    /// <summary>Computes the contract hash of every handler's contract (§6.2's whole-service domain projection unless <paramref name="isTopicScoped"/>).</summary>
    public static string Compute(IMessageHandlerDefinition[] handlers, bool isTopicScoped = false)
    {
        var document = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddMessageHandlerDefinitions(handlers)
            .Build();

        return Compute(document, isTopicScoped);
    }

    /// <summary>Computes the contract hash of <paramref name="document"/> (or projection of one).</summary>
    public static string Compute(EventServiceDocument document, bool isTopicScoped = false)
    {
        var json = document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidOperationException("Serialized EventServiceDocument did not parse as a JSON object.");

        Normalize(root, isTopicScoped);

        var canonical = JsonCanonicalizer.Canonicalize(root);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Normalize(JsonObject root, bool isTopicScoped)
    {
        root.Remove("messageEndpoint");
        root.Remove("transports");

        if (root["requests"] is JsonArray requests)
        {
            var reservedEntries = new List<JsonNode>();

            foreach (var requestNode in requests)
            {
                if (requestNode is not JsonObject request)
                {
                    continue;
                }

                request.Remove("example");

                // §6.2 requires reserved-ness to be evaluated (flag OR the `benzene:` prefix, per
                // §5.1) BEFORE the `reserved` flag itself is stripped - do that here, in that order.
                var reservedByFlag = request.TryGetPropertyValue("reserved", out var reservedNode)
                                      && reservedNode is JsonValue reservedValue
                                      && reservedValue.TryGetValue<bool>(out var reservedFlag)
                                      && reservedFlag;
                var topic = request["topic"]?.GetValue<string>();
                var isReserved = reservedByFlag || ReservedTopics.IsReserved(topic);

                request.Remove("reserved");

                if (isReserved)
                {
                    reservedEntries.Add(requestNode);
                }
            }

            // Only a whole-service (not topic-scoped, §5.3) document has its reserved entries
            // removed entirely - a topic-scoped document that explicitly asked for a reserved topic
            // keeps it (with only the flag already stripped above).
            if (!isTopicScoped)
            {
                foreach (var reservedEntry in reservedEntries)
                {
                    requests.Remove(reservedEntry);
                }
            }
        }

        if (root["events"] is JsonArray events)
        {
            foreach (var eventNode in events)
            {
                if (eventNode is JsonObject eventEntry)
                {
                    eventEntry.Remove("example");
                }
            }
        }
    }
}
