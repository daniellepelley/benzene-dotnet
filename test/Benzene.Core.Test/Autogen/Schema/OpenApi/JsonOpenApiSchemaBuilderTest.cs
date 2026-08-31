using Benzene.Schema.OpenApi;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi;

// #242: JsonOpenApiSchemaBuilder.CreateArraySchema called jToken.First() unconditionally when
// inferring a schema from an example JSON payload (the documented AddJsonEvent(topic, typeName, json)
// extension), so an ordinary empty example array anywhere in the payload crashed with
// InvalidOperationException: Sequence contains no elements.
public class JsonOpenApiSchemaBuilderTest
{
    [Fact]
    public void CreateSchema_EmptyExampleArray_DoesNotThrow_AndEmitsAnUntypedItemsPlaceholder()
    {
        // The exact review probe.
        var schemas = new JsonOpenApiSchemaBuilder().CreateSchema("OrderCreated", "{\"id\":\"abc\",\"tags\":[]}");

        var orderCreated = schemas["OrderCreated"];
        var tagsSchema = orderCreated.Properties["tags"];

        Assert.Equal("array", tagsSchema.Type);
        Assert.NotNull(tagsSchema.Items);
        // Untyped placeholder - no `type` keyword, since there was nothing in the example to infer one
        // from - not a guess at object/string/whatever.
        Assert.Null(tagsSchema.Items.Type);
    }

    [Fact]
    public void CreateSchema_NonEmptyArray_StillInfersItemSchemaFromFirstElement()
    {
        var schemas = new JsonOpenApiSchemaBuilder().CreateSchema("OrderCreated", "{\"id\":\"abc\",\"tags\":[\"urgent\"]}");

        var tagsSchema = schemas["OrderCreated"].Properties["tags"];

        Assert.Equal("array", tagsSchema.Type);
        Assert.Equal("string", tagsSchema.Items.Type);
    }

    // #264: Create's switch had no case for JTokenType.Float (an ordinary JSON decimal number) -
    // reachable from the documented public API EventServiceDocumentBuilder.AddJsonEvent - and threw
    // "No map for Float", aborting schema generation for the whole document.
    [Fact]
    public void CreateSchema_FloatExampleValue_DoesNotThrow_AndEmitsANumberSchema()
    {
        // The exact review probe.
        var schemas = new JsonOpenApiSchemaBuilder().CreateSchema("Order", "{\"price\":3.14}");

        var priceSchema = schemas["Order"].Properties["price"];

        Assert.Equal("number", priceSchema.Type);
    }

    // #264: Create's switch had no case for JTokenType.Null (an ordinary JSON null value) and threw
    // "No map for Null", aborting schema generation entirely for a document with any nullable field
    // captured as null in its example.
    [Fact]
    public void CreateSchema_NullExampleValue_DoesNotThrow_AndEmitsAnUntypedNullablePlaceholder()
    {
        // The exact review probe.
        var schemas = new JsonOpenApiSchemaBuilder().CreateSchema("Order", "{\"middleName\":null}");

        var middleNameSchema = schemas["Order"].Properties["middleName"];

        // Untyped placeholder - no `type` keyword, matching CreateArraySchema's "nothing in the
        // example to infer from" convention (#242) - not a guess at string/object/whatever.
        Assert.Null(middleNameSchema.Type);
        Assert.True(middleNameSchema.Nullable);
    }
}
