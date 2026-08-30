using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Benzene.Schema.OpenApi.Compatibility;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi.Compatibility;

/// <summary>
/// The JSON-Schema walker exists so the mesh aggregator can classify a change without taking an
/// OpenAPI dependency. Two walkers are tolerable; two <em>rule tables</em> would not be, because a
/// verdict that differed between the CI gate and the mesh screen would destroy the credibility of
/// both. <see cref="BothWalkers_ProduceIdenticalChangeSets"/> is the test that holds that invariant:
/// it drives the same schema pairs through both and asserts the kind/direction/path/description/verdict
/// tuples match exactly, in order.
/// </summary>
public class JsonSchemaComparerTest
{
    private const string Topic = "order:create";

    [Fact]
    public void IdenticalSchemas_ProduceNoChanges()
    {
        var schema = Json(("id", "string", true), ("total", "number", false));

        Assert.Empty(JsonSchemaComparer.Compare(schema, schema, SchemaDirection.Event, Topic, $"{Topic}.message"));
    }

    [Fact]
    public void NullOnEitherSide_ProducesNoChanges()
    {
        // "Not published at this version" is a statement about the catalogue, not about the contract.
        // Manufacturing a change here would turn an absence into a finding — the exact defect the
        // third state exists to prevent.
        var schema = Json(("id", "string", true));

        Assert.Empty(JsonSchemaComparer.Compare(null, schema, SchemaDirection.Request, Topic, "p"));
        Assert.Empty(JsonSchemaComparer.Compare(schema, null, SchemaDirection.Request, Topic, "p"));
    }

    [Fact]
    public void PropertyRemovedFromResponse_IsBreaking()
    {
        var baseline = Json(("id", "string", true), ("total", "number", false));
        var current = Json(("id", "string", true));

        var change = Assert.Single(JsonSchemaComparer.Compare(
            baseline, current, SchemaDirection.Response, Topic, $"{Topic}.response"));

        Assert.Equal(SchemaChangeKind.PropertyRemoved, change.Kind);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.Equal($"{Topic}.response.total", change.Path);
    }

    [Fact]
    public void PropertyRemovedFromRequest_IsOnlyAWarning()
    {
        var baseline = Json(("id", "string", true), ("line2", "string", false));
        var current = Json(("id", "string", true));

        var change = Assert.Single(JsonSchemaComparer.Compare(
            baseline, current, SchemaDirection.Request, Topic, $"{Topic}.request"));

        Assert.Equal(ChangeCompatibility.Warning, change.Compatibility);
    }

    [Fact]
    public void RequiredPropertyAddedToRequest_IsBreaking()
    {
        var baseline = Json(("id", "string", true));
        var current = Json(("id", "string", true), ("channel", "string", true));

        var change = Assert.Single(JsonSchemaComparer.Compare(
            baseline, current, SchemaDirection.Request, Topic, $"{Topic}.request"));

        Assert.Equal(SchemaChangeKind.RequiredPropertyAdded, change.Kind);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
    }

    [Fact]
    public void TypeChangeStopsTheWalk()
    {
        // The early return is deliberate in both walkers, and it means a type change HIDES every
        // change beneath it. The UI has to say so at that node, or its count is a floor presented as
        // a total — so this behaviour is pinned rather than merely tolerated.
        var baseline = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["nested"] = Json(("keep", "string", false), ("drop", "string", false)) },
        };
        var current = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["nested"] = new JsonObject { ["type"] = "string" },
            },
        };

        var change = Assert.Single(JsonSchemaComparer.Compare(
            baseline, current, SchemaDirection.Event, Topic, "m"));

        Assert.Equal(SchemaChangeKind.TypeChanged, change.Kind);
        Assert.Equal("m.nested", change.Path);
        Assert.DoesNotContain(JsonSchemaComparer.Compare(baseline, current, SchemaDirection.Event, Topic, "m"),
            c => c.Kind == SchemaChangeKind.PropertyRemoved);
    }

    [Fact]
    public void RenameSurfacesAsRemovedPlusAdded()
    {
        // The taxonomy has no rename concept and should not grow one — inferring intent from a
        // coincidence is a trap. The UI pairs these into a labelled hypothesis; the engine keeps both.
        var baseline = Json(("customerId", "string", true));
        var current = Json(("customerRef", "string", true));

        var changes = JsonSchemaComparer.Compare(baseline, current, SchemaDirection.Request, Topic, "r");

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == SchemaChangeKind.PropertyRemoved && c.Path == "r.customerId");
        Assert.Contains(changes, c => c.Kind == SchemaChangeKind.RequiredPropertyAdded && c.Path == "r.customerRef");
    }

    [Fact]
    public void CustomRulesAreHonoured()
    {
        var baseline = Json(("id", "string", true), ("line2", "string", false));
        var current = Json(("id", "string", true));
        var strict = SchemaCompatibilityRules.Default()
            .Set(SchemaChangeKind.PropertyRemoved, SchemaDirection.Request, ChangeCompatibility.Breaking);

        var change = Assert.Single(JsonSchemaComparer.Compare(
            baseline, current, SchemaDirection.Request, Topic, "r", strict));

        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
    }

    [Theory]
    [MemberData(nameof(EquivalenceCorpus))]
    public void BothWalkers_ProduceIdenticalChangeSets(
        string name, (string Name, string Type, bool Required)[] before, (string Name, string Type, bool Required)[] after)
    {
        // OpenAPI walker, driven through a whole document so it takes its real code path.
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocOf(Req(Topic, OpenApi(before), OpenApi(NoFields))),
            DocOf(Req(Topic, OpenApi(after), OpenApi(NoFields))));
        var viaOpenApi = openApiReport.Changes
            .Where(c => c.Direction == SchemaDirection.Request)
            .Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(Json(before), Json(after), SchemaDirection.Request, Topic, $"{Topic}.request")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.True(viaJson.Length > 0 || name == "identical", $"corpus case '{name}' detected nothing");
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_NestedArrayItems()
    {
        // The tuple-shaped EquivalenceCorpus above only expresses flat scalar properties, so array
        // recursion (both walkers' `items` handling) is exercised separately here - two levels deep
        // (array-of-array-of-object) so a single pass through "items" isn't mistaken for full
        // recursive coverage. A property is removed from the innermost object, nested inside both
        // array layers, and both walkers must land on the same "matrix[][].flag" path.
        var baselineRequest = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["matrix"] = new OpenApiSchema
                {
                    Type = "array",
                    Items = new OpenApiSchema { Type = "array", Items = MatrixCell(includeFlag: true) },
                },
            },
        };
        var currentRequest = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["matrix"] = new OpenApiSchema
                {
                    Type = "array",
                    Items = new OpenApiSchema { Type = "array", Items = MatrixCell(includeFlag: false) },
                },
            },
        };

        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocOf(Req(Topic, baselineRequest, OpenApi(NoFields))),
            DocOf(Req(Topic, currentRequest, OpenApi(NoFields))));
        var viaOpenApi = openApiReport.Changes
            .Where(c => c.Direction == SchemaDirection.Request)
            .Select(Tuple).ToArray();

        var baselineJson = MatrixJson(includeFlag: true);
        var currentJson = MatrixJson(includeFlag: false);

        var viaJson = JsonSchemaComparer
            .Compare(baselineJson, currentJson, SchemaDirection.Request, Topic, $"{Topic}.request")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Contains(viaJson, c => c.Item1 == SchemaChangeKind.PropertyRemoved && c.Item3 == $"{Topic}.request.matrix[][].flag");
    }

    // ---- oneOf / anyOf / allOf (union-aware walkers, WP-9) ----
    // Each case drives the same schema pair through both walkers and asserts identical tuples, the
    // same discipline as the corpus above and NestedArrayItems - plus the exact verdict the ruling's
    // breaking-direction table calls for, so a parity match alone can't hide both walkers agreeing on
    // the wrong answer.

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_UnionVariantRemoved_ResponseIsNonBreaking()
    {
        // The exact round-6 probe: oneOf:[Dog,Cat] response narrows to oneOf:[Dog]. Before WP-9 this
        // was reported as zero changes on both walkers. Per the ruling's table, a response variant
        // removal is non-breaking - consumers simply never see the removed variant again.
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), OneOfOpenApi("Dog", "Cat"))),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), OneOfOpenApi("Dog"))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(OneOfJson("Dog", "Cat"), OneOfJson("Dog"), SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, change.Item1);
        Assert.Equal(ChangeCompatibility.Compatible, change.Item5);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_UnionVariantAdded_RequestIsNonBreaking()
    {
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OneOfOpenApi("Dog"), OpenApi(NoFields))),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OneOfOpenApi("Dog", "Cat"), OpenApi(NoFields))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Request).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(OneOfJson("Dog"), OneOfJson("Dog", "Cat"), SchemaDirection.Request, Topic, $"{Topic}.request")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.UnionVariantAdded, change.Item1);
        Assert.Equal(ChangeCompatibility.Compatible, change.Item5);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_DiscriminatorMatching_ReorderedVariantsProduceNoChanges()
    {
        // Same two variants, same discriminator mapping, opposite order. Index-based matching would
        // pair baseline[0]=Dog against current[0]=Cat and report spurious changes; discriminator-value
        // matching must still pair Dog-with-Dog and Cat-with-Cat and find nothing.
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), DiscriminatedOneOfOpenApi("Dog", "Cat"))),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), DiscriminatedOneOfOpenApi("Cat", "Dog"))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(DiscriminatedOneOfJson("Dog", "Cat"), DiscriminatedOneOfJson("Cat", "Dog"), SchemaDirection.Response,
                Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Empty(viaJson);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_DiscriminatorMatching_InlineMembersReorderedProduceNoChanges()
    {
        // #239: both variants are inline - no $ref (and, for the JSON walker, no title either) - so
        // there is no ref-target name to key on and the discriminator mapping is the only identity
        // available. Before the fix, the mapping-fallback comparison in VariantKey compared a mapping
        // target against a refId that is guaranteed null on this branch, so it could never match and
        // every inline member fell through to purely positional matching: reordering the members (with
        // the mapping reordered right along with them) produced spurious changes for a byte-identical,
        // no-op reorder.
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(),
                Req(Topic, OpenApi(NoFields), DiscriminatedInlineOneOfOpenApi(("dog", "Dog"), ("cat", "Cat")))),
            DocWithComponents(PetComponentsOpenApi(),
                Req(Topic, OpenApi(NoFields), DiscriminatedInlineOneOfOpenApi(("cat", "Cat"), ("dog", "Dog")))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(
                DiscriminatedInlineOneOfJson(("dog", "Dog"), ("cat", "Cat")),
                DiscriminatedInlineOneOfJson(("cat", "Cat"), ("dog", "Dog")),
                SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Empty(viaJson);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_DiscriminatorMatching_InlineMemberPropertyRemoved_IsFlagged()
    {
        // Same shape, but a real change: the "dog" variant genuinely loses its distinguishing property.
        // The fix must still find it and attribute it to exactly one variant, not suppress it or smear
        // it across both.
        var baselineOpenApi = DiscriminatedInlineOneOfOpenApi(("dog", "Dog"), ("cat", "Cat"));
        var currentOpenApi = DiscriminatedInlineOneOfOpenApi(("dog", "Dog"), ("cat", "Cat"));
        ((OpenApiSchema)currentOpenApi.OneOf[0]).Properties.Clear();

        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), baselineOpenApi)),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), currentOpenApi)));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var baselineJson = DiscriminatedInlineOneOfJson(("dog", "Dog"), ("cat", "Cat"));
        var currentJson = DiscriminatedInlineOneOfJson(("dog", "Dog"), ("cat", "Cat"));
        ((JsonObject)currentJson["oneOf"]![0]!)["properties"] = new JsonObject();

        var viaJson = JsonSchemaComparer
            .Compare(baselineJson, currentJson, SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Equal(2, viaJson.Length);
        Assert.Contains(viaJson, c => c.Item1 == SchemaChangeKind.UnionVariantChanged);
        Assert.Contains(viaJson, c => c.Item1 == SchemaChangeKind.PropertyRemoved);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_DiscriminatorMappingCoverageAdded_ProducesNoSpuriousChange()
    {
        // The exact round-8 probe (#53): baseline oneOf:[Dog,Cat] maps only Cat; current same
        // oneOf:[Dog,Cat] adds a mapping entry for Dog too - nothing else about Dog changes. Before the
        // fix, coverage-keyed matching produced "ref:Dog" on the baseline side (unmapped) and "disc:dog"
        // on the current side (now mapped) for the very same $ref'd variant, so the pairwise matcher
        // reported a spurious UnionVariantRemoved+UnionVariantAdded pair for Dog - Breaking in either
        // direction per SchemaCompatibilityRules, so a harmless additive mapping edit failed the gate.
        // $ref-name-first matching fixes this: Dog keys on its $ref regardless of mapping coverage on
        // either side, stays matched, and nothing about Dog itself changed - so zero changes.
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(),
                Req(Topic, OpenApi(NoFields), PartiallyDiscriminatedOneOfOpenApi(["Dog", "Cat"], ["Cat"]))),
            DocWithComponents(PetComponentsOpenApi(),
                Req(Topic, OpenApi(NoFields), PartiallyDiscriminatedOneOfOpenApi(["Dog", "Cat"], ["Cat", "Dog"]))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(
                PartiallyDiscriminatedOneOfJson(["Dog", "Cat"], ["Cat"]),
                PartiallyDiscriminatedOneOfJson(["Dog", "Cat"], ["Cat", "Dog"]),
                SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Empty(viaJson);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_OneOfAndAllOfBothPresent_EachLosesAMember()
    {
        // #49: a schema carrying both oneOf and allOf on the same node, each losing a member in the
        // same edit. Verified correct by direct execution in the review round but had no regression
        // test - this pins it.
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(),
                Req(Topic, OpenApi(NoFields), OneOfAndAllOfOpenApi(["Dog", "Cat"], ["Dog", "Cat"]))),
            DocWithComponents(PetComponentsOpenApi(),
                Req(Topic, OpenApi(NoFields), OneOfAndAllOfOpenApi(["Dog"], ["Dog"]))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(OneOfAndAllOfJson(["Dog", "Cat"], ["Dog", "Cat"]), OneOfAndAllOfJson(["Dog"], ["Dog"]),
                SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Equal(2, viaJson.Length);
        Assert.Contains(viaJson, c => c.Item1 == SchemaChangeKind.UnionVariantRemoved && c.Item3.Contains(".oneOf["));
        Assert.Contains(viaJson, c => c.Item1 == SchemaChangeKind.UnionVariantRemoved && c.Item3.Contains(".allOf["));
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_NestedOneOfWithinOneOf_InnerVariantRemoved()
    {
        // #49: an outer oneOf variant that is itself a oneOf (nested union) losing an inner variant.
        // Verified correct by direct execution in the review round but had no regression test - this
        // pins it: the outer match (the "Wrapper" $ref is unchanged) still recurses into Wrapper's own
        // body and finds the inner removal, producing both an outer UnionVariantChanged and an inner
        // UnionVariantRemoved.
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsWithWrapperOpenApi("Dog", "Cat"),
                Req(Topic, OpenApi(NoFields), OneOfOpenApi("Wrapper"))),
            DocWithComponents(PetComponentsWithWrapperOpenApi("Dog"),
                Req(Topic, OpenApi(NoFields), OneOfOpenApi("Wrapper"))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(OneOfWithWrapperJson("Dog", "Cat"), OneOfWithWrapperJson("Dog"), SchemaDirection.Response,
                Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Equal(2, viaJson.Length);
        Assert.Equal(SchemaChangeKind.UnionVariantChanged, viaJson[0].Item1);
        Assert.Contains("Wrapper", viaJson[0].Item3);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, viaJson[1].Item1);
        Assert.Contains("Cat", viaJson[1].Item3);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_UnionVariantChanged_MatchedPairDiffers()
    {
        var currentComponents = PetComponentsOpenApi();
        currentComponents.Schemas["Dog"].Properties["size"] = new OpenApiSchema { Type = "string" };

        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), OneOfOpenApi("Dog", "Cat"))),
            DocWithComponents(currentComponents, Req(Topic, OpenApi(NoFields), OneOfOpenApi("Dog", "Cat"))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var baselineJson = OneOfJson("Dog", "Cat");
        var currentJson = OneOfJson("Dog", "Cat");
        ((JsonObject)currentJson["oneOf"]![0]!)["properties"]!["size"] = new JsonObject { ["type"] = "string" };

        var viaJson = JsonSchemaComparer
            .Compare(baselineJson, currentJson, SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);
        Assert.Equal(2, viaJson.Length);
        Assert.Equal(SchemaChangeKind.UnionVariantChanged, viaJson[0].Item1);
        Assert.Equal(SchemaChangeKind.PropertyAdded, viaJson[1].Item1);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_AllOfMemberRemoved_RequestIsBreaking()
    {
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, AllOfOpenApi("Dog", "Cat"), OpenApi(NoFields))),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, AllOfOpenApi("Dog"), OpenApi(NoFields))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Request).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(AllOfJson("Dog", "Cat"), AllOfJson("Dog"), SchemaDirection.Request, Topic, $"{Topic}.request")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, change.Item1);
        Assert.Equal(ChangeCompatibility.Breaking, change.Item5);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_AllOfMemberRemoved_ResponseIsNonBreaking()
    {
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), AllOfOpenApi("Dog", "Cat"))),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), AllOfOpenApi("Dog"))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(AllOfJson("Dog", "Cat"), AllOfJson("Dog"), SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, change.Item1);
        Assert.Equal(ChangeCompatibility.Compatible, change.Item5);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_AllOfMemberAdded_RequestIsNonBreaking()
    {
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, AllOfOpenApi("Dog"), OpenApi(NoFields))),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, AllOfOpenApi("Dog", "Cat"), OpenApi(NoFields))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Request).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(AllOfJson("Dog"), AllOfJson("Dog", "Cat"), SchemaDirection.Request, Topic, $"{Topic}.request")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.UnionVariantAdded, change.Item1);
        Assert.Equal(ChangeCompatibility.Compatible, change.Item5);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_AllOfMemberAdded_ResponseIsBreaking()
    {
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), AllOfOpenApi("Dog"))),
            DocWithComponents(PetComponentsOpenApi(), Req(Topic, OpenApi(NoFields), AllOfOpenApi("Dog", "Cat"))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var viaJson = JsonSchemaComparer
            .Compare(AllOfJson("Dog"), AllOfJson("Dog", "Cat"), SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.UnionVariantAdded, change.Item1);
        Assert.Equal(ChangeCompatibility.Breaking, change.Item5);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_ItemsAsymmetry_RequestIsBreaking()
    {
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocOf(Req(Topic, new OpenApiSchema { Type = "array" }, OpenApi(NoFields))),
            DocOf(Req(Topic, new OpenApiSchema { Type = "array", Items = new OpenApiSchema { Type = "string" } }, OpenApi(NoFields))));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Request).Select(Tuple).ToArray();

        var baselineJson = new JsonObject { ["type"] = "array" };
        var currentJson = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };

        var viaJson = JsonSchemaComparer
            .Compare(baselineJson, currentJson, SchemaDirection.Request, Topic, $"{Topic}.request")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.TypeChanged, change.Item1);
        Assert.Equal(ChangeCompatibility.Breaking, change.Item5);
    }

    [Fact]
    public void BothWalkers_ProduceIdenticalChangeSets_ItemsAsymmetry_ResponseIsBreaking()
    {
        var openApiReport = new SchemaCompatibilityComparer().Compare(
            DocOf(Req(Topic, OpenApi(NoFields), new OpenApiSchema { Type = "array", Items = new OpenApiSchema { Type = "string" } })),
            DocOf(Req(Topic, OpenApi(NoFields), new OpenApiSchema { Type = "array" })));
        var viaOpenApi = openApiReport.Changes.Where(c => c.Direction == SchemaDirection.Response).Select(Tuple).ToArray();

        var baselineJson = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        var currentJson = new JsonObject { ["type"] = "array" };

        var viaJson = JsonSchemaComparer
            .Compare(baselineJson, currentJson, SchemaDirection.Response, Topic, $"{Topic}.response")
            .Select(Tuple).ToArray();

        Assert.Equal(viaOpenApi, viaJson);

        var change = Assert.Single(viaJson);
        Assert.Equal(SchemaChangeKind.TypeChanged, change.Item1);
        Assert.Equal(ChangeCompatibility.Breaking, change.Item5);
    }

    private static OpenApiSchema MatrixCell(bool includeFlag)
    {
        var properties = new Dictionary<string, OpenApiSchema> { ["cell"] = new OpenApiSchema { Type = "string" } };
        if (includeFlag)
        {
            properties["flag"] = new OpenApiSchema { Type = "boolean" };
        }

        return new OpenApiSchema { Type = "object", Properties = properties };
    }

    private static JsonObject MatrixJson(bool includeFlag)
    {
        var cellProperties = new JsonObject { ["cell"] = new JsonObject { ["type"] = "string" } };
        if (includeFlag)
        {
            cellProperties["flag"] = new JsonObject { ["type"] = "boolean" };
        }

        var cell = new JsonObject { ["type"] = "object", ["properties"] = cellProperties };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["matrix"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "array", ["items"] = cell },
                },
            },
        };
    }

    public static TheoryData<string, (string, string, bool)[], (string, string, bool)[]> EquivalenceCorpus() => new()
    {
        { "identical", [("id", "string", true)], [("id", "string", true)] },
        { "optional added", [("id", "string", true)], [("id", "string", true), ("note", "string", false)] },
        { "required added", [("id", "string", true)], [("id", "string", true), ("channel", "string", true)] },
        { "removed", [("id", "string", true), ("total", "number", false)], [("id", "string", true)] },
        { "became required", [("id", "string", false)], [("id", "string", true)] },
        { "became optional", [("id", "string", true)], [("id", "string", false)] },
        { "type changed", [("amount", "integer", true)], [("amount", "number", true)] },
        { "renamed", [("customerId", "string", true)], [("customerRef", "string", true)] },
        {
            "several at once",
            [("customerId", "string", true), ("total", "number", false), ("amount", "integer", true)],
            [("customerRef", "string", true), ("amount", "number", true), ("channel", "string", true)]
        },
    };

    private static readonly (string Name, string Type, bool Required)[] NoFields = [];

    private static (SchemaChangeKind, SchemaDirection, string, string, ChangeCompatibility) Tuple(SchemaChange c) =>
        (c.Kind, c.Direction, c.Path, c.Description, c.Compatibility) is var (k, d, p, desc, comp)
            ? (k, d, p, desc is null ? string.Empty : desc, comp)
            : default;

    private static JsonObject Json(params (string Name, string Type, bool Required)[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Name] = new JsonObject { ["type"] = field.Type };
            if (field.Required)
            {
                required.Add(field.Name);
            }
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static OpenApiSchema OpenApi(params (string Name, string Type, bool Required)[] fields)
    {
        var schema = new OpenApiSchema { Type = "object", Properties = new Dictionary<string, OpenApiSchema>() };
        foreach (var field in fields)
        {
            schema.Properties[field.Name] = new OpenApiSchema { Type = field.Type };
            if (field.Required)
            {
                schema.Required.Add(field.Name);
            }
        }

        return schema;
    }

    private static RequestResponse Req(string topic, OpenApiSchema request, OpenApiSchema response) =>
        new() { Topic = topic, Version = "", Request = request, Response = response };

    private static EventServiceDocument DocOf(params RequestResponse[] requests) =>
        DocWithComponents(new OpenApiComponents { Schemas = new Dictionary<string, OpenApiSchema>() }, requests);

    private static EventServiceDocument DocWithComponents(OpenApiComponents components, params RequestResponse[] requests) =>
        new(new OpenApiInfo(), [], requests, [], components);

    // ---- oneOf / anyOf / allOf helpers (WP-9) ----
    // "Dog" and "Cat" are the fixed pair used throughout: each carries one distinguishing property
    // (sound) so a matched-pair recursion has something to find when a test deliberately changes one.

    private static OpenApiSchema RefSchema(string name) =>
        new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = name } };

    private static OpenApiSchema OneOfOpenApi(params string[] names) => new() { OneOf = names.Select(RefSchema).ToList() };

    private static OpenApiSchema AllOfOpenApi(params string[] names) => new() { AllOf = names.Select(RefSchema).ToList() };

    private static OpenApiSchema DiscriminatedOneOfOpenApi(params string[] names) => new()
    {
        OneOf = names.Select(RefSchema).ToList(),
        Discriminator = new OpenApiDiscriminator
        {
            PropertyName = "petType",
            Mapping = names.ToDictionary(n => n.ToLowerInvariant(), n => $"#/components/schemas/{n}")
        }
    };

    private static OpenApiComponents PetComponentsOpenApi() => new()
    {
        Schemas = new Dictionary<string, OpenApiSchema>
        {
            ["Dog"] = PetOpenApi(),
            ["Cat"] = PetOpenApi()
        }
    };

    private static OpenApiSchema PetOpenApi() =>
        new() { Type = "object", Properties = new Dictionary<string, OpenApiSchema> { ["sound"] = new() { Type = "string" } } };

    private static JsonObject PetJson(string name) => new()
    {
        ["type"] = "object",
        ["$ref"] = $"#/components/schemas/{name}",
        ["properties"] = new JsonObject { ["sound"] = new JsonObject { ["type"] = "string" } }
    };

    private static JsonObject OneOfJson(params string[] names) =>
        new() { ["oneOf"] = new JsonArray(names.Select(n => (JsonNode)PetJson(n)).ToArray()) };

    private static JsonObject AllOfJson(params string[] names) =>
        new() { ["allOf"] = new JsonArray(names.Select(n => (JsonNode)PetJson(n)).ToArray()) };

    private static JsonObject DiscriminatedOneOfJson(params string[] names)
    {
        var mapping = new JsonObject();
        foreach (var name in names)
        {
            mapping[name.ToLowerInvariant()] = $"#/components/schemas/{name}";
        }

        return new JsonObject
        {
            ["oneOf"] = new JsonArray(names.Select(n => (JsonNode)PetJson(n)).ToArray()),
            ["discriminator"] = new JsonObject { ["propertyName"] = "petType", ["mapping"] = mapping }
        };
    }

    // ---- inline (no $ref, no title) discriminator-mapped member helpers (#239) ----
    // Each pair is (mapping key, distinguishing property name) so a test can tell which inline member
    // a matched-pair recursion landed on without any $ref/title identity to read back.

    private static OpenApiSchema InlinePetOpenApi(string distinguishingProperty) => new()
    {
        Type = "object",
        Properties = new Dictionary<string, OpenApiSchema> { [distinguishingProperty] = new() { Type = "boolean" } }
    };

    private static OpenApiSchema DiscriminatedInlineOneOfOpenApi(params (string MappingKey, string DistinguishingProperty)[] members) => new()
    {
        OneOf = members.Select(m => InlinePetOpenApi(m.DistinguishingProperty)).Cast<OpenApiSchema>().ToList(),
        Discriminator = new OpenApiDiscriminator
        {
            PropertyName = "petType",
            Mapping = members.ToDictionary(m => m.MappingKey, m => m.MappingKey)
        }
    };

    private static JsonObject InlinePetJson(string distinguishingProperty) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { [distinguishingProperty] = new JsonObject { ["type"] = "boolean" } }
    };

    private static JsonObject DiscriminatedInlineOneOfJson(params (string MappingKey, string DistinguishingProperty)[] members)
    {
        var mapping = new JsonObject();
        foreach (var member in members)
        {
            mapping[member.MappingKey] = member.MappingKey;
        }

        return new JsonObject
        {
            ["oneOf"] = new JsonArray(members.Select(m => (JsonNode)InlinePetJson(m.DistinguishingProperty)).ToArray()),
            ["discriminator"] = new JsonObject { ["propertyName"] = "petType", ["mapping"] = mapping }
        };
    }

    // ---- discriminator-coverage / oneOf+allOf / nested-union helpers (#53, #49) ----

    /// <summary>Like <see cref="DiscriminatedOneOfOpenApi"/> but the mapping covers only
    /// <paramref name="mappedNames"/> - a subset of <paramref name="names"/> - so a test can add mapping
    /// coverage for a previously-unmapped <c>$ref</c>d variant without changing anything else about it.</summary>
    private static OpenApiSchema PartiallyDiscriminatedOneOfOpenApi(string[] names, string[] mappedNames) => new()
    {
        OneOf = names.Select(RefSchema).ToList(),
        Discriminator = new OpenApiDiscriminator
        {
            PropertyName = "petType",
            Mapping = mappedNames.ToDictionary(n => n.ToLowerInvariant(), n => $"#/components/schemas/{n}")
        }
    };

    private static JsonObject PartiallyDiscriminatedOneOfJson(string[] names, string[] mappedNames)
    {
        var mapping = new JsonObject();
        foreach (var name in mappedNames)
        {
            mapping[name.ToLowerInvariant()] = $"#/components/schemas/{name}";
        }

        return new JsonObject
        {
            ["oneOf"] = new JsonArray(names.Select(n => (JsonNode)PetJson(n)).ToArray()),
            ["discriminator"] = new JsonObject { ["propertyName"] = "petType", ["mapping"] = mapping }
        };
    }

    private static OpenApiSchema OneOfAndAllOfOpenApi(string[] oneOfNames, string[] allOfNames) => new()
    {
        OneOf = oneOfNames.Select(RefSchema).ToList(),
        AllOf = allOfNames.Select(RefSchema).ToList()
    };

    private static JsonObject OneOfAndAllOfJson(string[] oneOfNames, string[] allOfNames) => new()
    {
        ["oneOf"] = new JsonArray(oneOfNames.Select(n => (JsonNode)PetJson(n)).ToArray()),
        ["allOf"] = new JsonArray(allOfNames.Select(n => (JsonNode)PetJson(n)).ToArray())
    };

    /// <summary>Components including a "Wrapper" schema whose own body is a <c>oneOf</c> over
    /// <paramref name="innerNames"/> - a oneOf variant that is itself a oneOf, for the nested-union
    /// corpus case.</summary>
    private static OpenApiComponents PetComponentsWithWrapperOpenApi(params string[] innerNames)
    {
        var components = PetComponentsOpenApi();
        components.Schemas["Wrapper"] = new OpenApiSchema { Type = "object", OneOf = innerNames.Select(RefSchema).ToList() };
        return components;
    }

    private static JsonObject WrapperJson(params string[] innerNames) => new()
    {
        ["type"] = "object",
        ["$ref"] = "#/components/schemas/Wrapper",
        ["oneOf"] = new JsonArray(innerNames.Select(n => (JsonNode)PetJson(n)).ToArray())
    };

    private static JsonObject OneOfWithWrapperJson(params string[] wrapperInnerNames) =>
        new() { ["oneOf"] = new JsonArray(WrapperJson(wrapperInnerNames)) };
}
