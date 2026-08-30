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
}
