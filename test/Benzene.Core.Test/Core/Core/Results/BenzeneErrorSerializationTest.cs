using Benzene.Abstractions.Results;
using Xunit;
using StjJsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;
using NewtonsoftJsonSerializer = Benzene.NewtonsoftJson.JsonSerializer;

namespace Benzene.Test.Core.Core.Results;

/// <summary>
/// Round-trip coverage for <see cref="BenzeneError"/> across the two serializers exercised at merge
/// time by work/benzene-result-errors-ruling.md R2 (System.Text.Json - the process default - and
/// Newtonsoft.Json), plus R3's <c>[JsonIgnore(Condition = WhenWritingNull)]</c> null-omission
/// contract (verified against the default STJ serializer specifically; Newtonsoft's own
/// <c>NullValueHandling</c> is unrelated and untouched by this change).
/// </summary>
public class BenzeneErrorSerializationTest
{
    private static readonly BenzeneError WithFieldAndCode = new("Name must not be empty", "Name", "NotEmptyValidator");
    private static readonly BenzeneError MessageOnly = new("Something went wrong");

    [Fact]
    public void Stj_RoundTrip_PreservesMessageFieldAndCode()
    {
        var serializer = new StjJsonSerializer();

        var json = serializer.Serialize(WithFieldAndCode);
        var roundTripped = serializer.Deserialize<BenzeneError>(json);

        Assert.Equal(WithFieldAndCode, roundTripped);
    }

    [Fact]
    public void Stj_Serialize_UsesCamelCaseWireNames()
    {
        var json = new StjJsonSerializer().Serialize(WithFieldAndCode);

        Assert.Contains("\"message\"", json);
        Assert.Contains("\"field\"", json);
        Assert.Contains("\"code\"", json);
    }

    [Fact]
    public void Stj_Serialize_OmitsNullFieldAndCode()
    {
        // R3: [JsonIgnore(Condition = WhenWritingNull)] on Field/Code - a message-only error must not
        // clutter the wire with "field":null,"code":null.
        var json = new StjJsonSerializer().Serialize(MessageOnly);

        Assert.DoesNotContain("\"field\"", json);
        Assert.DoesNotContain("\"code\"", json);
        Assert.Contains("\"message\"", json);
    }

    [Fact]
    public void Stj_RoundTrip_MessageOnly_FieldAndCodeStayNull()
    {
        var serializer = new StjJsonSerializer();

        var json = serializer.Serialize(MessageOnly);
        var roundTripped = serializer.Deserialize<BenzeneError>(json);

        Assert.Equal(MessageOnly, roundTripped);
        Assert.Null(roundTripped.Field);
        Assert.Null(roundTripped.Code);
    }

    [Fact]
    public void Newtonsoft_RoundTrip_PreservesMessageFieldAndCode()
    {
        var serializer = new NewtonsoftJsonSerializer();

        var json = serializer.Serialize(WithFieldAndCode);
        var roundTripped = serializer.Deserialize<BenzeneError>(json);

        Assert.Equal(WithFieldAndCode, roundTripped);
    }

    [Fact]
    public void Newtonsoft_RoundTrip_MessageOnly_FieldAndCodeStayNull()
    {
        var serializer = new NewtonsoftJsonSerializer();

        var json = serializer.Serialize(MessageOnly);
        var roundTripped = serializer.Deserialize<BenzeneError>(json);

        Assert.Equal(MessageOnly, roundTripped);
        Assert.Null(roundTripped.Field);
        Assert.Null(roundTripped.Code);
    }
}
