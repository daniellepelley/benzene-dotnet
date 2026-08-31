using System;
using System.Collections.Generic;
using System.IO;
using Benzene.CodeGen.Core;
using Benzene.CodeGen.Core.Writers;
using Benzene.CodeGen.Markdown;
using Benzene.Schema.OpenApi.Examples;
using Benzene.Test.Autogen.CodeGen.Helpers;
using Benzene.Test.Autogen.CodeGen.Model;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Markdown;

public class MarkdownTypeBuilderTest
{
    private string LoadExpected(string fileName) =>
        File.ReadAllText($"{Directory.GetCurrentDirectory()}/Autogen/CodeGen/Markdown/Examples/{fileName}.md");

    [Fact]
    public void BuildType_GetUserMessage_Test()
    {
        var expected = LoadExpected("GetTenantMessage");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "tenant:get", (typeof(GetTenantMessage), typeof(GetTenantMessage), typeof(TenantDto)) },
            { "tenant:create", (typeof(CreateTenantMessage), typeof(CreateTenantMessage), typeof(TenantDto)) }
        };

        var lineWriter = new LineWriter();
        
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter( dictionary.ToOpenApiSchemas()));

        markdownTypeBuilder.BuildType("GetTenantMessage", lineWriter);

        Assert.Equal(expected, lineWriter.GetLines().ToText(), ignoreLineEndingDifferences: true);
    }
    
    [Fact]
    public void BuildType_TenantDto_Test()
    {
        var expected = LoadExpected("TenantDto");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "tenant:get", (typeof(GetTenantMessage), typeof(GetTenantMessage), typeof(TenantDto)) },
            { "tenant:create", (typeof(CreateTenantMessage), typeof(CreateTenantMessage), typeof(TenantDto)) }
        };

        var lineWriter = new LineWriter();
        
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter( dictionary.ToOpenApiSchemas()));

        markdownTypeBuilder.BuildType("TenantDto", lineWriter);

        Assert.Equal(expected, lineWriter.GetLines().ToText(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildType_ArrayOfReferencedObjects_ExpandsItemProperties()
    {
        // A property that is an array of a referenced object (List<TenantDto>) should render each
        // item's fields, exactly like the single-object case does. The buggy `||` in MapProperty
        // collapsed every referenced-object array to "{...}[]" (short-circuiting on Reference != null),
        // so the item's fields were lost from the generated docs.
        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "tenant:list", (typeof(GetTenantMessage), typeof(GetTenantMessage), typeof(TenantListDto)) }
        };

        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(dictionary.ToOpenApiSchemas()));

        markdownTypeBuilder.BuildType("TenantListDto", lineWriter);

        var expected =
            "{" + Environment.NewLine +
            "    tenants: {" + Environment.NewLine +
            "        id: guid" + Environment.NewLine +
            "        name: string" + Environment.NewLine +
            "        crn: string" + Environment.NewLine +
            "        internal: {" + Environment.NewLine +
            "            value1: string" + Environment.NewLine +
            "            value2: {...}" + Environment.NewLine +
            "        }" + Environment.NewLine +
            "    }[]" + Environment.NewLine +
            "}" + Environment.NewLine;

        Assert.Equal(expected, lineWriter.GetLines().ToText(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildType_ArrayOfInlineObjects_DoesNotThrow()
    {
        // An array whose items are an inline object (no $ref) reaches the array branch via
        // Items.Type == "object" with Items.Reference == null. The buggy `||` then evaluated
        // Items.Reference.ReferenceV2 on a null Reference -> NullReferenceException. The item's
        // inline fields should be expanded instead.
        var itemSchema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                { "name", new OpenApiSchema { Type = "string" } }
            }
        };
        var rootSchema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                { "items", new OpenApiSchema { Type = "array", Items = itemSchema } }
            }
        };

        var schemas = new Dictionary<string, OpenApiSchema> { { "Root", rootSchema } };
        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(schemas));

        markdownTypeBuilder.BuildType("Root", lineWriter);

        Assert.Contains("name: string", lineWriter.GetLines().ToText());
    }

    private static OpenApiSchema Ref(string id) => new()
    {
        Reference = new OpenApiReference { Id = id, Type = ReferenceType.Schema }
    };

    [Fact]
    public void BuildType_OneOfProperty_WithNoSharedBase_RendersAUnionListing_NotBlank()
    {
        // A oneOf property (no own .Type) fell through MapProperty's generic fallback branch to
        // GetPropertyTypeName, which had no oneOf handling and returned openApiSchema.Type - null,
        // rendered as "payment: " with nothing after it.
        var schemas = new Dictionary<string, OpenApiSchema>
        {
            ["CardPayment"] = new() { Type = "object", Properties = new Dictionary<string, OpenApiSchema> { ["cardNumber"] = new() { Type = "string" } } },
            ["BankPayment"] = new() { Type = "object", Properties = new Dictionary<string, OpenApiSchema> { ["iban"] = new() { Type = "string" } } },
            ["Root"] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["payment"] = new() { OneOf = new List<OpenApiSchema> { Ref("CardPayment"), Ref("BankPayment") } }
                }
            }
        };

        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(schemas));
        markdownTypeBuilder.BuildType("Root", lineWriter);

        var text = lineWriter.GetLines().ToText();

        Assert.DoesNotContain("payment: " + Environment.NewLine, text);
        Assert.Contains("payment: oneOf: {CardPayment|BankPayment}", text);
    }

    // #213: an array schema with Items == null isn't reachable through Benzene's own SchemaBuilder,
    // but MapProperty is reached via the public BuildType and can be handed any hand-authored/
    // deserialized schema - it used to NRE on `openApiSchema.Items.Reference`.
    [Fact]
    public void BuildType_ArraySchemaWithNullItems_HandledGracefully_NotNullReferenceException()
    {
        var rootSchema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                { "tags", new OpenApiSchema { Type = "array", Items = null } }
            }
        };

        var schemas = new Dictionary<string, OpenApiSchema> { { "Root", rootSchema } };
        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(schemas));

        markdownTypeBuilder.BuildType("Root", lineWriter);

        Assert.Contains("tags: Void[]", lineWriter.GetLines().ToText());
    }

    [Fact]
    public void BuildType_OneOfProperty_MembersShareAnAllOfBase_RendersTheSharedBaseTypeName()
    {
        var schemas = new Dictionary<string, OpenApiSchema>
        {
            ["PaymentMethod"] = new() { Type = "object", Properties = new Dictionary<string, OpenApiSchema> { ["currency"] = new() { Type = "string" } } },
            ["CardPayment"] = new()
            {
                Type = "object",
                AllOf = new List<OpenApiSchema> { Ref("PaymentMethod") },
                Properties = new Dictionary<string, OpenApiSchema> { ["cardNumber"] = new() { Type = "string" } }
            },
            ["BankPayment"] = new()
            {
                Type = "object",
                AllOf = new List<OpenApiSchema> { Ref("PaymentMethod") },
                Properties = new Dictionary<string, OpenApiSchema> { ["iban"] = new() { Type = "string" } }
            },
            ["Root"] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["payment"] = new() { OneOf = new List<OpenApiSchema> { Ref("CardPayment"), Ref("BankPayment") } }
                }
            }
        };

        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(schemas));
        markdownTypeBuilder.BuildType("Root", lineWriter);

        Assert.Contains("payment: PaymentMethod", lineWriter.GetLines().ToText());
    }

    // #265: a property whose schema is type:object with additionalProperties but no own declared
    // properties (the shape for a Dictionary<string, T>-typed property) fell into MapProperty's
    // empty-object else branch, which hard-coded a bare "{}" with the property NAME dropped
    // entirely - unreadable, unattributable output.
    [Fact]
    public void MapProperty_AdditionalPropertiesMap_RendersTheNamedMapShape_NotABareAnonymousBraces()
    {
        var rootSchema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                {
                    "scores", new OpenApiSchema
                    {
                        Type = "object",
                        AdditionalProperties = new OpenApiSchema { Type = "integer" }
                    }
                }
            }
        };

        var schemas = new Dictionary<string, OpenApiSchema> { { "Root", rootSchema } };
        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(schemas));

        markdownTypeBuilder.BuildType("Root", lineWriter);

        var text = lineWriter.GetLines().ToText();

        // Before the fix: an unnamed, unattributable "{}" line - the field's name never appeared.
        Assert.DoesNotContain("    {}" + Environment.NewLine, text);
        Assert.Contains("scores: {[string]: int}", text);
    }

    // Same #265 fix, for an array of such map-shaped objects (Dictionary<string, T>[]).
    [Fact]
    public void MapProperty_ArrayOfAdditionalPropertiesMaps_RendersTheNamedMapShape()
    {
        var itemSchema = new OpenApiSchema
        {
            Type = "object",
            AdditionalProperties = new OpenApiSchema { Type = "string" }
        };
        var rootSchema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                { "scoresPerRound", new OpenApiSchema { Type = "array", Items = itemSchema } }
            }
        };

        var schemas = new Dictionary<string, OpenApiSchema> { { "Root", rootSchema } };
        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(schemas));

        markdownTypeBuilder.BuildType("Root", lineWriter);

        var text = lineWriter.GetLines().ToText();

        Assert.DoesNotContain("    {}[]" + Environment.NewLine, text);
        Assert.Contains("scoresPerRound: {[string]: string}[]", text);
    }

    [Fact]
    public void BuildType_ArrayProperty_WithNullItems_RendersVoidArrayPlaceholder_NotNRE()
    {
        // #213: MapProperty's array branch dereferenced Items.Reference/Items.Type unconditionally,
        // NRE-ing on a hand-authored schema with Items == null. GetPropertyTypeName already
        // null-checks the equivalent case (its own array branch recurses into GetPropertyTypeName(Items),
        // and that method's very first check turns a null schema into the "Void" placeholder) -
        // MapProperty now guards the same way and falls through to the generic fallback branch,
        // which renders that same placeholder via GetPropertyTypeName instead of throwing.
        var rootSchema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                { "items", new OpenApiSchema { Type = "array", Items = null } }
            }
        };

        var schemas = new Dictionary<string, OpenApiSchema> { { "Root", rootSchema } };
        var lineWriter = new LineWriter();
        var markdownTypeBuilder = new MarkdownTypeBuilder(new SchemaGetter(schemas));

        markdownTypeBuilder.BuildType("Root", lineWriter);

        Assert.Contains("items: Void[]", lineWriter.GetLines().ToText());
    }
}
