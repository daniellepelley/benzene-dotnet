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
}
