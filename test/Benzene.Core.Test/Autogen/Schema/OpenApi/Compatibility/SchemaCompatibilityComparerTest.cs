using System;
using System.Collections.Generic;
using System.Linq;
using Benzene.Schema.OpenApi.Compatibility;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi.Compatibility;

public class SchemaCompatibilityComparerTest
{
    private const string Topic = "order:create";

    [Fact]
    public void IdenticalDocuments_AreCompatible_WithNoChanges()
    {
        var doc = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false), ("status", false))));

        var report = new SchemaCompatibilityComparer().Compare(doc, doc);

        Assert.Empty(report.Changes);
        Assert.True(report.IsCompatible);
        Assert.Equal(ChangeCompatibility.Compatible, report.Overall);
    }

    [Fact]
    public void ResponsePropertyAdded_IsCompatible()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false))));
        var current = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false), ("status", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.PropertyAdded, change.Kind);
        Assert.Equal(SchemaDirection.Response, change.Direction);
        Assert.Equal(ChangeCompatibility.Compatible, change.Compatibility);
        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void ResponsePropertyRemoved_IsBreaking()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false), ("status", false))));
        var current = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.PropertyRemoved, change.Kind);
        Assert.Equal(SchemaDirection.Response, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.False(report.IsCompatible);
        Assert.True(report.HasBreakingChanges);
    }

    [Fact]
    public void RequestRequiredPropertyAdded_IsBreaking()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false))));
        var current = DocOf(Req(Topic, Obj(("id", true), ("customerId", true)), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.RequiredPropertyAdded, change.Kind);
        Assert.Equal(SchemaDirection.Request, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.False(report.IsCompatible);
    }

    [Fact]
    public void RequestOptionalPropertyAdded_IsCompatible()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false))));
        var current = DocOf(Req(Topic, Obj(("id", true), ("note", false)), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.PropertyAdded, change.Kind);
        Assert.Equal(ChangeCompatibility.Compatible, change.Compatibility);
        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void PropertyTypeChanged_IsBreaking()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", true)), WithProp("total", "string")));
        var current = DocOf(Req(Topic, Obj(("id", true)), WithProp("total", "integer")));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.TypeChanged, change.Kind);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.False(report.IsCompatible);
    }

    [Fact]
    public void TopicRemoved_IsBreaking()
    {
        var baseline = DocOf(
            Req(Topic, Obj(("id", true)), Obj(("id", false))),
            Req("order:cancel", Obj(("id", true)), Obj(("id", false))));
        var current = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.TopicRemoved, change.Kind);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.False(report.IsCompatible);
    }

    [Fact]
    public void TopicAdded_IsCompatible()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false))));
        var current = DocOf(
            Req(Topic, Obj(("id", true)), Obj(("id", false))),
            Req("order:cancel", Obj(("id", true)), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.TopicAdded, change.Kind);
        Assert.Equal(ChangeCompatibility.Compatible, change.Compatibility);
        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void CustomRule_CanDowngradeBreakingToWarning()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false), ("status", false))));
        var current = DocOf(Req(Topic, Obj(("id", true)), Obj(("id", false))));

        var rules = SchemaCompatibilityRules.Default()
            .Set(SchemaChangeKind.PropertyRemoved, SchemaDirection.Response, ChangeCompatibility.Warning);

        var report = new SchemaCompatibilityComparer(rules).Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(ChangeCompatibility.Warning, change.Compatibility);
        Assert.True(report.IsCompatible);   // no longer breaking
        Assert.True(report.HasWarnings);
    }

    // ---- oneOf / anyOf / allOf (union-aware walkers, WP-9) ----

    [Fact]
    public void ResponseOneOfVariantRemoved_RoundSixProbe_IsUnionVariantRemoved_NonBreaking()
    {
        // The exact round-6 probe: oneOf:[Dog,Cat] response narrows to oneOf:[Dog]. Before WP-9 this
        // was reported as zero changes. Per the ruling's breaking-direction table, a response variant
        // removal is non-breaking - consumers simply never see the removed variant again.
        var baseline = DocOf(PetComponents(), Req(Topic, Obj(("id", true)), OneOfRef("Dog", "Cat")));
        var current = DocOf(PetComponents(), Req(Topic, Obj(("id", true)), OneOfRef("Dog")));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, change.Kind);
        Assert.Equal(SchemaDirection.Response, change.Direction);
        Assert.Equal(ChangeCompatibility.Compatible, change.Compatibility);
        Assert.Contains("Cat", change.Path); // "Dog" stays matched; "Cat" is the one reported removed
        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void RequestOneOfVariantRemoved_IsBreaking()
    {
        var baseline = DocOf(PetComponents(), Req(Topic, OneOfRef("Dog", "Cat"), Obj(("id", false))));
        var current = DocOf(PetComponents(), Req(Topic, OneOfRef("Dog"), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, change.Kind);
        Assert.Equal(SchemaDirection.Request, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.False(report.IsCompatible);
    }

    [Fact]
    public void RequestOneOfVariantAdded_IsUnionVariantAdded_NonBreaking()
    {
        var baseline = DocOf(PetComponents(), Req(Topic, OneOfRef("Dog"), Obj(("id", false))));
        var current = DocOf(PetComponents(), Req(Topic, OneOfRef("Dog", "Cat"), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantAdded, change.Kind);
        Assert.Equal(SchemaDirection.Request, change.Direction);
        Assert.Equal(ChangeCompatibility.Compatible, change.Compatibility);
        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void ResponseOneOfVariantAdded_IsBreaking()
    {
        var baseline = DocOf(PetComponents(), Req(Topic, Obj(("id", true)), OneOfRef("Dog")));
        var current = DocOf(PetComponents(), Req(Topic, Obj(("id", true)), OneOfRef("Dog", "Cat")));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantAdded, change.Kind);
        Assert.Equal(SchemaDirection.Response, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.False(report.IsCompatible);
    }

    [Fact]
    public void OneOfDiscriminator_ReorderedVariants_MatchByDiscriminatorNotIndex()
    {
        // Same two variants, same discriminator mapping, but listed in the opposite order. Index-based
        // matching would pair baseline[0]=Dog against current[0]=Cat and report spurious changes;
        // discriminator-value matching must still pair Dog-with-Dog and Cat-with-Cat and find nothing.
        var baseline = DocOf(PetComponents(), Req(Topic, Obj(("id", true)), DiscriminatedOneOf("Dog", "Cat")));
        var current = DocOf(PetComponents(), Req(Topic, Obj(("id", true)), DiscriminatedOneOf("Cat", "Dog")));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        Assert.Empty(report.Changes);
    }

    [Fact]
    public void OneOfDiscriminator_MappingCoverageAdded_ProducesNoSpuriousChange()
    {
        // The exact round-8 probe (#53): baseline oneOf:[Dog,Cat] maps only Cat; current same
        // oneOf:[Dog,Cat] adds a mapping entry for Dog too - nothing else about Dog changes. Before the
        // fix, coverage-keyed matching produced "ref:Dog" on the baseline side (unmapped) and "disc:dog"
        // on the current side (now mapped) for the very same $ref'd variant, so the pairwise matcher
        // reported a spurious UnionVariantRemoved+UnionVariantAdded pair for Dog - Breaking in either
        // direction per SchemaCompatibilityRules, so a harmless additive mapping edit failed the gate.
        var baselineMapping = new OpenApiDiscriminator
        {
            PropertyName = "petType",
            Mapping = new Dictionary<string, string> { ["cat"] = "#/components/schemas/Cat" }
        };
        var currentMapping = new OpenApiDiscriminator
        {
            PropertyName = "petType",
            Mapping = new Dictionary<string, string>
            {
                ["cat"] = "#/components/schemas/Cat",
                ["dog"] = "#/components/schemas/Dog"
            }
        };

        var baseline = DocOf(PetComponents(), Req(Topic, Obj(("id", true)),
            new OpenApiSchema { OneOf = OneOfRef("Dog", "Cat").OneOf, Discriminator = baselineMapping }));
        var current = DocOf(PetComponents(), Req(Topic, Obj(("id", true)),
            new OpenApiSchema { OneOf = OneOfRef("Dog", "Cat").OneOf, Discriminator = currentMapping }));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        Assert.Empty(report.Changes);
    }

    [Fact]
    public void OneOfVariantMatchedPairDiffers_IsUnionVariantChanged_WithNestedChange()
    {
        var baselineComponents = PetComponents();
        var currentComponents = PetComponents();
        currentComponents.Schemas["Dog"].Properties["size"] = new OpenApiSchema { Type = "string" };

        var baseline = DocOf(baselineComponents, Req(Topic, Obj(("id", true)), OneOfRef("Dog", "Cat")));
        var current = DocOf(currentComponents, Req(Topic, Obj(("id", true)), OneOfRef("Dog", "Cat")));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        Assert.Equal(2, report.Changes.Count);
        Assert.Equal(SchemaChangeKind.UnionVariantChanged, report.Changes[0].Kind);
        Assert.Equal(SchemaChangeKind.PropertyAdded, report.Changes[1].Kind);
        Assert.Contains("Dog", report.Changes[0].Path);
        Assert.StartsWith(report.Changes[0].Path, report.Changes[1].Path);
    }

    [Fact]
    public void AllOfMemberRemoved_Request_IsBreaking()
    {
        var baseline = DocOf(PetComponents(), Req(Topic, AllOfRef("Dog", "Cat"), Obj(("id", false))));
        var current = DocOf(PetComponents(), Req(Topic, AllOfRef("Dog"), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, change.Kind);
        Assert.Equal(SchemaDirection.Request, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
    }

    [Fact]
    public void AllOfMemberRemoved_Response_IsNonBreaking()
    {
        var baseline = DocOf(PetComponents(), Req(Topic, Obj(("id", false)), AllOfRef("Dog", "Cat")));
        var current = DocOf(PetComponents(), Req(Topic, Obj(("id", false)), AllOfRef("Dog")));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantRemoved, change.Kind);
        Assert.Equal(SchemaDirection.Response, change.Direction);
        Assert.Equal(ChangeCompatibility.Compatible, change.Compatibility);
    }

    [Fact]
    public void AllOfMemberAdded_Request_IsNonBreaking()
    {
        var baseline = DocOf(PetComponents(), Req(Topic, AllOfRef("Dog"), Obj(("id", false))));
        var current = DocOf(PetComponents(), Req(Topic, AllOfRef("Dog", "Cat"), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantAdded, change.Kind);
        Assert.Equal(SchemaDirection.Request, change.Direction);
        Assert.Equal(ChangeCompatibility.Compatible, change.Compatibility);
    }

    [Fact]
    public void AllOfMemberAdded_Response_IsBreaking()
    {
        var baseline = DocOf(PetComponents(), Req(Topic, Obj(("id", false)), AllOfRef("Dog")));
        var current = DocOf(PetComponents(), Req(Topic, Obj(("id", false)), AllOfRef("Dog", "Cat")));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.UnionVariantAdded, change.Kind);
        Assert.Equal(SchemaDirection.Response, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
    }

    [Fact]
    public void ItemsAppearsOnOneSide_Request_IsBreakingTypeChange()
    {
        var baseline = DocOf(Req(Topic, ArrayOf(null), Obj(("id", false))));
        var current = DocOf(Req(Topic, ArrayOf(new OpenApiSchema { Type = "string" }), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.TypeChanged, change.Kind);
        Assert.Equal(SchemaDirection.Request, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
        Assert.EndsWith("[]", change.Path);
    }

    [Fact]
    public void ItemsDisappearsOnOneSide_Response_IsBreakingTypeChange()
    {
        var baseline = DocOf(Req(Topic, Obj(("id", false)), ArrayOf(new OpenApiSchema { Type = "string" })));
        var current = DocOf(Req(Topic, Obj(("id", false)), ArrayOf(null)));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.TypeChanged, change.Kind);
        Assert.Equal(SchemaDirection.Response, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
    }

    // #168: CompareSchemas never recursed into additionalProperties, so a breaking change entirely
    // inside a Dictionary<string, T>-shaped schema's value type passed `benzene diff` as "No changes".

    [Fact]
    public void AdditionalPropertiesValueSchema_BreakingChangeInside_IsDetected()
    {
        var baselineComponents = new OpenApiComponents
        {
            Schemas = new Dictionary<string, OpenApiSchema>
            {
                ["Address"] = WithProp("street", "string")
            }
        };
        var currentComponents = new OpenApiComponents
        {
            Schemas = new Dictionary<string, OpenApiSchema>
            {
                // Both a type change ("street": string -> integer) and a new required property
                // ("city") inside the map's value schema - either alone is breaking.
                ["Address"] = new OpenApiSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        ["street"] = new OpenApiSchema { Type = "integer" },
                        ["city"] = new OpenApiSchema { Type = "string" }
                    },
                    Required = new HashSet<string> { "city" }
                }
            }
        };

        var baseline = DocOf(baselineComponents, Req(Topic, Obj(("id", false)), MapOf(RefTo("Address"))));
        var current = DocOf(currentComponents, Req(Topic, Obj(("id", false)), MapOf(RefTo("Address"))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        Assert.Equal(ChangeCompatibility.Breaking, report.Overall);
        Assert.Contains(report.Changes, c => c.Kind == SchemaChangeKind.TypeChanged && c.Path.EndsWith("street"));
        Assert.Contains(report.Changes, c => c.Kind == SchemaChangeKind.RequiredPropertyAdded && c.Path.EndsWith("city"));
    }

    [Fact]
    public void AdditionalPropertiesAppearsOnOneSide_Request_IsBreakingTypeChange()
    {
        var baseline = DocOf(Req(Topic, MapOf(null), Obj(("id", false))));
        var current = DocOf(Req(Topic, MapOf(new OpenApiSchema { Type = "string" }), Obj(("id", false))));

        var report = new SchemaCompatibilityComparer().Compare(baseline, current);

        var change = Assert.Single(report.Changes);
        Assert.Equal(SchemaChangeKind.TypeChanged, change.Kind);
        Assert.Equal(SchemaDirection.Request, change.Direction);
        Assert.Equal(ChangeCompatibility.Breaking, change.Compatibility);
    }

    [Fact]
    public void AdditionalPropertiesUnchanged_NoChangesReported()
    {
        var doc = DocOf(Req(Topic, Obj(("id", false)), MapOf(new OpenApiSchema { Type = "string" })));

        var report = new SchemaCompatibilityComparer().Compare(doc, doc);

        Assert.Empty(report.Changes);
    }

    // ---- helpers ----

    private static EventServiceDocument DocOf(params RequestResponse[] requests) =>
        DocOf(new OpenApiComponents { Schemas = new Dictionary<string, OpenApiSchema>() }, requests);

    private static EventServiceDocument DocOf(OpenApiComponents components, params RequestResponse[] requests) =>
        new EventServiceDocument(
            new OpenApiInfo(),
            Array.Empty<OpenApiTag>(),
            requests,
            Array.Empty<Event>(),
            components);

    private static RequestResponse Req(string topic, OpenApiSchema request, OpenApiSchema response) =>
        new RequestResponse { Topic = topic, Version = "", Request = request, Response = response };

    private static OpenApiSchema ArrayOf(OpenApiSchema? items) => new OpenApiSchema { Type = "array", Items = items };

    private static OpenApiSchema MapOf(OpenApiSchema? valueSchema) =>
        new OpenApiSchema { Type = "object", AdditionalProperties = valueSchema };

    private static OpenApiSchema RefTo(string name) =>
        new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = name } };

    private static OpenApiSchema OneOfRef(params string[] names) =>
        new OpenApiSchema { OneOf = names.Select(RefTo).ToList() };

    private static OpenApiSchema AllOfRef(params string[] names) =>
        new OpenApiSchema { AllOf = names.Select(RefTo).ToList() };

    private static OpenApiSchema DiscriminatedOneOf(params string[] names) =>
        new OpenApiSchema
        {
            OneOf = names.Select(RefTo).ToList(),
            Discriminator = new OpenApiDiscriminator
            {
                PropertyName = "petType",
                Mapping = names.ToDictionary(n => n.ToLowerInvariant(), n => $"#/components/schemas/{n}")
            }
        };

    private static OpenApiComponents PetComponents() => new OpenApiComponents
    {
        Schemas = new Dictionary<string, OpenApiSchema>
        {
            ["Dog"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema> { ["bark"] = new OpenApiSchema { Type = "boolean" } }
            },
            ["Cat"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema> { ["meow"] = new OpenApiSchema { Type = "boolean" } }
            }
        }
    };

    private static OpenApiSchema Obj(params (string Name, bool Required)[] props)
    {
        var schema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>(),
            Required = new HashSet<string>()
        };

        foreach (var prop in props)
        {
            schema.Properties[prop.Name] = new OpenApiSchema { Type = "string" };
            if (prop.Required)
            {
                schema.Required.Add(prop.Name);
            }
        }

        return schema;
    }

    private static OpenApiSchema WithProp(string name, string type) =>
        new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema> { [name] = new OpenApiSchema { Type = type } },
            Required = new HashSet<string>()
        };
}
