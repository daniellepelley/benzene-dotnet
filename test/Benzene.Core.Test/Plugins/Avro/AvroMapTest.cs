using System;
using System.Collections.Generic;
using Benzene.Avro;
using Xunit;

namespace Benzene.Test.Plugins.Avro;

/// <summary>
/// Regression tests for #278: <see cref="AvroDatumConverter"/> had no <c>Schema.Type.Map</c> switch
/// arm at all - any Avro <c>map</c> field crashed on deserialize (primitive values) or serialize
/// (complex values), reachable through the package's own advertised "explicit/registered schema" use
/// case (<see cref="AvroOptions.RegisterSchema{T}"/>). Avro map keys are always strings per spec; a
/// non-string-keyed CLR dictionary target must throw <see cref="NotSupportedException"/> rather than
/// silently coercing keys.
/// </summary>
public class AvroMapTest
{
    public class PrimitiveMapHolder
    {
        public Dictionary<string, string> Tags { get; set; } = new();
    }

    private const string PrimitiveMapSchema = """
    { "type":"record","name":"PrimitiveMapHolder",
      "fields":[{"name":"Tags","type":{"type":"map","values":"string"}}] }
    """;

    [Fact]
    public void RoundTrips_PrimitiveValuedMap()
    {
        var serializer = new AvroSerializer(new AvroOptions().RegisterSchema<PrimitiveMapHolder>(PrimitiveMapSchema));
        var sample = new PrimitiveMapHolder { Tags = { ["env"] = "prod", ["region"] = "eu-west-1" } };

        var payload = serializer.Serialize(sample);
        var result = serializer.Deserialize<PrimitiveMapHolder>(payload);

        Assert.NotNull(result);
        Assert.Equal(sample.Tags, result!.Tags);
    }

    public class InnerRecord
    {
        public int Id { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class OuterRecord
    {
        public Dictionary<string, List<InnerRecord>> Buckets { get; set; } = new();
    }

    private const string OuterSchema = """
    {
      "type": "record", "name": "OuterRecord",
      "fields": [ { "name": "Buckets", "type": { "type": "map", "values":
        { "type": "array", "items": { "type": "record", "name": "InnerRecord",
          "fields": [ {"name":"Id","type":"int"}, {"name":"Label","type":"string"} ] } } } } ]
    }
    """;

    [Fact]
    public void RoundTrips_RecordWithinArrayWithinMap()
    {
        var serializer = new AvroSerializer(new AvroOptions().RegisterSchema<OuterRecord>(OuterSchema));
        var sample = new OuterRecord
        {
            Buckets =
            {
                ["a"] = new List<InnerRecord> { new() { Id = 1, Label = "one" }, new() { Id = 2, Label = "two" } },
                ["b"] = new List<InnerRecord>()
            }
        };

        var payload = serializer.Serialize(sample);
        var result = serializer.Deserialize<OuterRecord>(payload);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Buckets.Count);
        Assert.Equal(2, result.Buckets["a"].Count);
        Assert.Equal(1, result.Buckets["a"][0].Id);
        Assert.Equal("one", result.Buckets["a"][0].Label);
        Assert.Equal(2, result.Buckets["a"][1].Id);
        Assert.Empty(result.Buckets["b"]);
    }

    public class NonStringKeyedMapHolder
    {
        public Dictionary<int, string> Tags { get; set; } = new();
    }

    private const string NonStringKeyedMapSchema = """
    { "type":"record","name":"NonStringKeyedMapHolder",
      "fields":[{"name":"Tags","type":{"type":"map","values":"string"}}] }
    """;

    [Fact]
    public void Serialize_NonStringKeyedDictionaryTarget_ThrowsNotSupportedException()
    {
        // Avro maps are always string-keyed per spec; the schema itself has no way to say otherwise,
        // so this is caught by inspecting the CLR value's actual dictionary key type.
        var serializer = new AvroSerializer(new AvroOptions().RegisterSchema<NonStringKeyedMapHolder>(NonStringKeyedMapSchema));
        var sample = new NonStringKeyedMapHolder { Tags = { [1] = "one" } };

        Assert.Throws<NotSupportedException>(() => serializer.Serialize(sample));
    }

    [Fact]
    public void Deserialize_NonStringKeyedDictionaryTarget_ThrowsNotSupportedException()
    {
        // Legal wire bytes for a string-keyed map, deserialized against a CLR type declaring a
        // non-string-keyed dictionary target - must be rejected, not silently coerced.
        var writer = new AvroSerializer(new AvroOptions().RegisterSchema<PrimitiveMapHolder>(PrimitiveMapSchema));
        var payload = writer.Serialize(new PrimitiveMapHolder { Tags = { ["1"] = "one" } });

        var reader = new AvroSerializer(new AvroOptions().RegisterSchema<NonStringKeyedMapHolder>(NonStringKeyedMapSchema));

        Assert.Throws<NotSupportedException>(() => reader.Deserialize<NonStringKeyedMapHolder>(payload));
    }
}
