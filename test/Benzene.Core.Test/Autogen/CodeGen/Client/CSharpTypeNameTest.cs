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
}
