using System.Collections.Generic;
using Benzene.CodeGen.Client;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

public class CSharpTypeNameTest
{
    private readonly CSharpTypeName _typeName = new();

    [Fact]
    public void GetName_PlainObjectWithoutAdditionalProperties_DoesNotThrow()
    {
        // A "type": "object" schema with no additionalProperties leaves the property null; reading its
        // .Type used to throw a NullReferenceException.
        var schema = new OpenApiSchema { Type = "object" };

        Assert.Equal("object", _typeName.GetName(schema));
    }

    [Fact]
    public void GetName_StringMap_IsDictionaryOfString()
    {
        var schema = new OpenApiSchema
        {
            Type = "object",
            AdditionalProperties = new OpenApiSchema { Type = "string" }
        };

        Assert.Equal("Dictionary<string, string>", _typeName.GetName(schema));
    }

    [Fact]
    public void GetName_Int32_IsInt()
    {
        Assert.Equal("int", _typeName.GetName(new OpenApiSchema { Type = "integer" }));
        Assert.Equal("int", _typeName.GetName(new OpenApiSchema { Type = "integer", Format = "int32" }));
    }

    [Fact]
    public void GetName_Int64_IsLong_NotTruncatedToInt()
    {
        // An int64-format integer must be a long, or a generated client silently truncates 64-bit values.
        Assert.Equal("long", _typeName.GetName(new OpenApiSchema { Type = "integer", Format = "int64" }));
        Assert.Equal("long?", _typeName.GetName(new OpenApiSchema { Type = "integer", Format = "int64", Nullable = true }));
    }

    private static OpenApiSchema Ref(string id) => new()
    {
        Reference = new OpenApiReference { Id = id, Type = ReferenceType.Schema }
    };

    [Fact]
    public void GetName_TopLevelOneOf_WithNoSharedBaseDiscoverable_FallsBackToObject_NotNull()
    {
        // A bare top-level oneOf (e.g. a polymorphic request/response schema, not wrapped in a
        // named $ref) used to fall straight through to `return openApiSchema.Type;`, which is null
        // for a schema with no .Type - producing uncompilable code like `Task<IBenzeneResult<>>`.
        // With no schema catalogue available at this call site, "object" (always compiles) is the
        // correct fallback when a shared base can't be discovered from the member schemas alone.
        var schema = new OpenApiSchema
        {
            OneOf = new List<OpenApiSchema> { Ref("CardPayment"), Ref("BankPayment") }
        };

        var name = _typeName.GetName(schema);

        Assert.NotNull(name);
        Assert.Equal("object", name);
    }

    [Fact]
    public void GetName_TopLevelOneOf_AllMembersShareAnAllOfBase_UsesTheSharedBase()
    {
        var schema = new OpenApiSchema
        {
            OneOf = new List<OpenApiSchema>
            {
                new() { Reference = new OpenApiReference { Id = "CardPayment", Type = ReferenceType.Schema }, AllOf = new List<OpenApiSchema> { Ref("PaymentMethod") } },
                new() { Reference = new OpenApiReference { Id = "BankPayment", Type = ReferenceType.Schema }, AllOf = new List<OpenApiSchema> { Ref("PaymentMethod") } },
            }
        };

        Assert.Equal("PaymentMethod", _typeName.GetName(schema));
    }

    [Fact]
    public void GetName_TopLevelAnyOf_FallsBackToObject_NotNull()
    {
        var schema = new OpenApiSchema
        {
            AnyOf = new List<OpenApiSchema> { Ref("CardPayment"), Ref("BankPayment") }
        };

        Assert.Equal("object", _typeName.GetName(schema));
    }

    [Fact]
    public void GetName_RefWithArbitraryCatalogueId_IsFormattedTheSameWayTheClassDeclarationIs()
    {
        // #240: SuppliedSchemaCatalog schema ids are arbitrary caller strings. The class declaration
        // for a catalogue entry ("orderItem") is emitted through CSharpNameFormatter.Format
        // (OpenApiSchemaCSharpTypeBuilder), producing "OrderItem" - but GetName used to return
        // Reference.Id raw and unformatted, so a property typed from the very same $ref generated
        // `public orderItem Item { get; set; }`, a straight CS0246 (the class "orderItem" was never
        // generated - only "OrderItem" was).
        var formatter = new CSharpNameFormatter();

        Assert.Equal(formatter.Format("orderItem"), _typeName.GetName(Ref("orderItem")));
    }

    [Fact]
    public void GetName_RefWithHyphenatedCatalogueId_IsAValidCSharpIdentifier()
    {
        // A hyphenated id ("order-item") returned raw is not merely mismatched with the class name -
        // it's a hard C# syntax error (a bare "-" inside a type reference).
        var name = _typeName.GetName(Ref("order-item"));

        Assert.DoesNotContain("-", name);
        Assert.Equal(new CSharpNameFormatter().Format("order-item"), name);
    }

    [Fact]
    public void GetArrayType_RefWithArbitraryCatalogueId_IsFormattedTheSameWayTheClassDeclarationIs()
    {
        // Same bug, reached via the array branch (GetArrayType) instead: `orderItem[]` instead of
        // `OrderItem[]`.
        var schema = new OpenApiSchema { Type = "array", Items = Ref("orderItem") };

        Assert.Equal("OrderItem[]", _typeName.GetName(schema));
    }

    [Fact]
    public void GetName_RefToAnEnumSchema_ResolvesToTheEnumTypeName_NotEmptyOrTheRefWrapper()
    {
        // #166: a property referencing an enum-shaped schema (Swashbuckle's shape for a real C#
        // enum) is, at this call site, just a $ref placeholder - Type/Enum live on the catalogue
        // entry, not here. GetName must resolve it to the enum's own name (matching
        // OpenApiSchemaCSharpTypeBuilder, which now emits a real `enum` under that exact name)
        // rather than resolving the reference before/instead of recognising what it points at.
        var schema = Ref("OrderStatus");

        Assert.Equal("OrderStatus", _typeName.GetName(schema));
    }
}
