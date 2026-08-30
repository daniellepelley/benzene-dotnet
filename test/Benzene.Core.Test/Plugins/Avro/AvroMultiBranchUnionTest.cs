using Benzene.Avro;
using Xunit;

namespace Benzene.Test.Plugins.Avro;

/// <summary>
/// Regression tests for #279: <c>AvroDatumConverter.NonNullBranch</c> always picked the FIRST non-null
/// branch of a union, correct only for the common 2-branch <c>["null", X]</c> "optional field" shape.
/// For a union with 3+ non-null branches this silently miscoded the value through the wrong branch's
/// (de)serializer - e.g. a <c>bool</c>/<c>long</c> value went in and the STRING branch's coercion came
/// back out, losing both the original CLR type and (for some combinations) the value itself. The fix
/// resolves the branch by the value's actual runtime type on write, and by the datum's actual runtime
/// type (as already resolved by the underlying Avro reader) on read.
/// </summary>
public class AvroMultiBranchUnionTest
{
    public class MultiUnionRecord
    {
        public object? Value { get; set; }
    }

    private const string MultiUnionSchema = """
    { "type":"record","name":"MultiUnionRecord",
      "fields":[{"name":"Value","type":["null","string","long","boolean"]}] }
    """;

    private static AvroSerializer CreateSerializer() =>
        new(new AvroOptions().RegisterSchema<MultiUnionRecord>(MultiUnionSchema));

    [Fact]
    public void RoundTrips_BooleanValue_ThroughAThreePlusBranchUnion()
    {
        var serializer = CreateSerializer();

        var result = serializer.Deserialize<MultiUnionRecord>(serializer.Serialize(new MultiUnionRecord { Value = true }));

        Assert.NotNull(result);
        Assert.IsType<bool>(result!.Value);
        Assert.Equal(true, result.Value);
    }

    [Fact]
    public void RoundTrips_LongValue_ThroughAThreePlusBranchUnion()
    {
        var serializer = CreateSerializer();

        var result = serializer.Deserialize<MultiUnionRecord>(serializer.Serialize(new MultiUnionRecord { Value = 42L }));

        Assert.NotNull(result);
        Assert.IsType<long>(result!.Value);
        Assert.Equal(42L, result.Value);
    }

    [Fact]
    public void RoundTrips_StringValue_ThroughAThreePlusBranchUnion()
    {
        var serializer = CreateSerializer();

        var result = serializer.Deserialize<MultiUnionRecord>(serializer.Serialize(new MultiUnionRecord { Value = "hello" }));

        Assert.NotNull(result);
        Assert.IsType<string>(result!.Value);
        Assert.Equal("hello", result.Value);
    }

    // ---------- 2-branch nullable-union regression: must stay byte-identical ----------

    public class OptionalStringHolder
    {
        public string? Value { get; set; }
    }

    [Fact]
    public void TwoBranchNullableUnion_ReferenceTypeValuePresent_StillRoundTrips()
    {
        // AvroSchemaGenerator's own reflected schema for a nullable reference-typed property is the
        // common ["null", X] shape this converter has always handled correctly - the runtime-type
        // branch resolution added for #279 must not change this case at all.
        var serializer = new AvroSerializer();
        var sample = new OptionalStringHolder { Value = "present" };

        var result = serializer.Deserialize<OptionalStringHolder>(serializer.Serialize(sample));

        Assert.NotNull(result);
        Assert.Equal("present", result!.Value);
    }

    [Fact]
    public void TwoBranchNullableUnion_ReferenceTypeValueNull_StillRoundTrips()
    {
        var serializer = new AvroSerializer();
        var sample = new OptionalStringHolder { Value = null };

        var result = serializer.Deserialize<OptionalStringHolder>(serializer.Serialize(sample));

        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    public class OptionalIntHolder
    {
        public int? Value { get; set; }
    }

    [Fact]
    public void TwoBranchNullableUnion_ValueTypePresent_StillRoundTrips()
    {
        var serializer = new AvroSerializer();
        var sample = new OptionalIntHolder { Value = 7 };

        var result = serializer.Deserialize<OptionalIntHolder>(serializer.Serialize(sample));

        Assert.NotNull(result);
        Assert.Equal(7, result!.Value);
    }

    [Fact]
    public void TwoBranchNullableUnion_ValueTypeNull_StillRoundTrips()
    {
        var serializer = new AvroSerializer();
        var sample = new OptionalIntHolder { Value = null };

        var result = serializer.Deserialize<OptionalIntHolder>(serializer.Serialize(sample));

        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }
}
