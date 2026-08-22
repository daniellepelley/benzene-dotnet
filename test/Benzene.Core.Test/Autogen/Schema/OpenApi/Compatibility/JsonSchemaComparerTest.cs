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
        new(new OpenApiInfo(), [], requests, [],
            new OpenApiComponents { Schemas = new Dictionary<string, OpenApiSchema>() });
}
