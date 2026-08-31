using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Test.Mesh.Wire;

public class MeshSchemaGeneratorTest
{
    [Theory]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(char), "string")]
    [InlineData(typeof(System.Guid), "string")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(byte), "integer")]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(long), "integer")]
    [InlineData(typeof(float), "number")]
    [InlineData(typeof(double), "number")]
    [InlineData(typeof(decimal), "number")]
    public void Derive_PrimitiveType_MapsToTheExpectedJsonSchemaType(System.Type type, string expectedType)
    {
        var schema = MeshSchemaGenerator.Derive(type);

        Assert.Equal(expectedType, schema["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(typeof(System.DateTime))]
    [InlineData(typeof(System.DateTimeOffset))]
    public void Derive_DateTimeType_MapsToStringWithDateTimeFormat(System.Type type)
    {
        var schema = MeshSchemaGenerator.Derive(type);

        Assert.Equal("string", schema["type"]!.GetValue<string>());
        Assert.Equal("date-time", schema["format"]!.GetValue<string>());
    }

    [Fact]
    public void Derive_ByteArray_MapsToString()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(byte[]));

        Assert.Equal("string", schema["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(System.Text.Json.JsonElement))]
    [InlineData(typeof(DayOfWeekForSchema))]
    public void Derive_UnknowableShapeType_MapsToAnUnconstrainedSchema(System.Type type)
    {
        var schema = MeshSchemaGenerator.Derive(type);

        Assert.Empty(schema);
    }

    [Fact]
    public void Derive_NullableValueType_AddsNullToTheUnderlyingSchemaType()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(int?));

        var types = schema["type"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "integer", "null" }, types);
    }

    [Fact]
    public void Derive_StringKeyedDictionary_MapsToObjectWithAdditionalProperties()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(Dictionary<string, int>));

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("integer", schema["additionalProperties"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Derive_StringKeyedDictionary_SchemaIsByteIdentical_NoDescriptorHashChurnFromTheFix()
    {
        // #235 pins the string-keyed path unchanged - extending TryGetDictionaryValueType to other key
        // types must not alter the schema an already-correct contract derives (no descriptor-hash
        // churn). The known-good shape, asserted verbatim rather than field-by-field.
        var schema = MeshSchemaGenerator.Derive(typeof(Dictionary<string, int>));

        Assert.Equal(
            "{\"type\":\"object\",\"additionalProperties\":{\"type\":\"integer\"}}",
            schema.ToJsonString());
    }

    [Theory]
    [InlineData(typeof(Dictionary<int, string>))]
    [InlineData(typeof(Dictionary<DayOfWeekForSchema, string>))]
    [InlineData(typeof(Dictionary<System.Guid, string>))]
    public void Derive_NonStringKeyedDictionary_StillMapsToObjectWithAdditionalProperties_NotAnArray(System.Type type)
    {
        // #235: System.Text.Json serializes ANY dictionary (int/enum/Guid-keyed included) as a JSON
        // object with string-converted keys, never an array of {key,value} pairs. Before the fix, a
        // non-string key type fell through to the enumerable fallback and produced the wrong "array"
        // shape the wire never actually emits.
        var schema = MeshSchemaGenerator.Derive(type);

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("string", schema["additionalProperties"]!["type"]!.GetValue<string>());
        Assert.Null(schema["items"]); // must not also carry the array-shape's sibling key
    }

    [Fact]
    public void Derive_Array_MapsToArrayWithItems()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(int[]));

        Assert.Equal("array", schema["type"]!.GetValue<string>());
        Assert.Equal("integer", schema["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Derive_GenericList_MapsToArrayWithItems()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(List<string>));

        Assert.Equal("array", schema["type"]!.GetValue<string>());
        Assert.Equal("string", schema["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Derive_ClassWithProperties_RequiredExcludesNullableAnnotatedProperties()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(SamplePerson));

        var properties = schema["properties"]!.AsObject();
        Assert.Equal("string", properties["name"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", properties["age"]!["type"]!.GetValue<string>());
        Assert.True(properties.ContainsKey("nickname"));
        Assert.True(properties.ContainsKey("score"));

        var required = schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Contains("name", required);
        Assert.Contains("age", required);
        Assert.DoesNotContain("nickname", required); // nullable reference type
        Assert.DoesNotContain("score", required); // Nullable<int>
    }

    [Fact]
    public void Derive_JsonPropertyNameAttribute_RenamesTheProperty()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(SamplePerson));

        var properties = schema["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("custom_name"));
        Assert.False(properties.ContainsKey("renamed"));
    }

    [Fact]
    public void Derive_JsonIgnoreAlways_ExcludesTheProperty()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(SamplePerson));

        var properties = schema["properties"]!.AsObject();
        Assert.False(properties.ContainsKey("secret"));
    }

    [Fact]
    public void Derive_JsonIgnoreWhenWritingDefault_MakesAnOtherwiseRequiredPropertyOptional()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(SamplePerson));

        var required = schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("count", required);
    }

    [Fact]
    public void Derive_Properties_AreEmittedInLexicographicOrder()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(OutOfOrderProperties));

        var keys = schema["properties"]!.AsObject().Select(x => x.Key).ToArray();
        Assert.Equal(keys.OrderBy(x => x, System.StringComparer.Ordinal).ToArray(), keys);
    }

    [Fact]
    public void Derive_Required_IsEmittedInOrdinalOrder_RegardlessOfDeclarationOrder()
    {
        // Regression for #7: reflection order is unspecified across runtimes, so "required" must be sorted
        // the same way "properties" already is (StringComparer.Ordinal), not left in declaration order -
        // otherwise the descriptor hash isn't reproducible.
        var schema = MeshSchemaGenerator.Derive(typeof(OutOfOrderRequiredProperties));

        var required = schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "alpha", "mike", "zeta" }, required);
    }

    [Fact]
    public void Derive_RecursiveType_CutsTheCycleWithAnUnconstrainedSchema()
    {
        var schema = MeshSchemaGenerator.Derive(typeof(RecursiveNode));

        var childSchema = schema["properties"]!["child"]!.AsObject();
        Assert.Empty(childSchema);
    }

    private enum DayOfWeekForSchema
    {
        Monday,
        Tuesday
    }

    private class SamplePerson
    {
        public string Name { get; set; } = string.Empty;

        public string? Nickname { get; set; }

        public int Age { get; set; }

        public int? Score { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Count { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
        public string Secret { get; set; } = string.Empty;

        [JsonPropertyName("custom_name")]
        public string Renamed { get; set; } = string.Empty;
    }

    private class OutOfOrderProperties
    {
        public string Zeta { get; set; } = string.Empty;
        public string Alpha { get; set; } = string.Empty;
        public string Mike { get; set; } = string.Empty;
    }

    private class OutOfOrderRequiredProperties
    {
        public string Zeta { get; set; } = string.Empty;
        public string Alpha { get; set; } = string.Empty;
        public string Mike { get; set; } = string.Empty;
    }

    private class RecursiveNode
    {
        public string Id { get; set; } = string.Empty;

        public RecursiveNode? Child { get; set; }
    }
}
