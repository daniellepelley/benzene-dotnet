using System.Buffers;
using System.Text;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.Test.Examples;
using Xunit;

namespace Benzene.Test.Core.Core.Serialization;

public class JsonSerializerTest
{
    [Fact]
    public void Serialize_ByteAndStringPaths_ProduceByteIdenticalJson()
    {
        var serializer = new JsonSerializer();
        var payload = new ExampleRequestPayload { Id = 42, Name = "some-name" };

        var expected = serializer.Serialize(typeof(ExampleRequestPayload), payload);

        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(typeof(ExampleRequestPayload), payload, writer);
        var actual = Encoding.UTF8.GetString(writer.WrittenSpan);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Deserialize_ByteAndStringPaths_ProduceEquivalentObjects()
    {
        var serializer = new JsonSerializer();
        var json = serializer.Serialize(new ExampleRequestPayload { Id = 42, Name = "some-name" });

        var viaString = serializer.Deserialize<ExampleRequestPayload>(json);
        var viaBytes = (ExampleRequestPayload)serializer.Deserialize(typeof(ExampleRequestPayload), Encoding.UTF8.GetBytes(json));

        Assert.Equal(viaString.Id, viaBytes.Id);
        Assert.Equal(viaString.Name, viaBytes.Name);
    }

    [Fact]
    public void JsonSerializer_ImplementsIPayloadSerializer()
    {
        Assert.IsAssignableFrom<Benzene.Abstractions.Serialization.IPayloadSerializer>(new JsonSerializer());
    }

    [Fact]
    public void Serialize_DefaultOptions_DoesNotEscapeWireUnfriendlyCharacters()
    {
        // Benzene JSON goes to API clients/browsers, never HTML, so the default relaxed encoder must
        // write <, >, & and ' literally rather than as \uXXXX escapes - otherwise framework wire
        // messages (e.g. a NotFound detail carrying the "<missing>" topic sentinel) render as gibberish.
        var serializer = new JsonSerializer();

        var json = serializer.Serialize(new { detail = "No handler found for topic '<missing>' & more" });

        Assert.Contains("'<missing>'", json);
        Assert.Contains("&", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void Serialize_BytePath_AlsoUsesRelaxedEncoding()
    {
        var serializer = new JsonSerializer();
        var payload = new { detail = "topic '<missing>'" };

        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(payload.GetType(), payload, writer);
        var json = Encoding.UTF8.GetString(writer.WrittenSpan);

        Assert.Contains("'<missing>'", json);
        Assert.DoesNotContain("\\u", json);
    }

    // #59: System.Text.Json throws ArgumentException serializing NaN/Infinity/-Infinity by default
    // (standard JSON has no numeric representation for them), diverging from Benzene.NewtonsoftJson's
    // serializer, which has always tolerated them (encoding as the quoted strings "NaN" etc.). The
    // default options now set AllowNamedFloatingPointLiterals so this serializer no longer crashes on
    // them - and, verified empirically, produces the same quoted-string wire form Newtonsoft always has.
    [Theory]
    [InlineData(double.NaN, "\"NaN\"")]
    [InlineData(double.PositiveInfinity, "\"Infinity\"")]
    [InlineData(double.NegativeInfinity, "\"-Infinity\"")]
    public void Serialize_NamedFloatingPointLiteral_DoesNotThrow_AndRoundTrips(double value, string expectedJson)
    {
        var serializer = new JsonSerializer();

        var json = serializer.Serialize(value);

        Assert.Equal(expectedJson, json);
        Assert.Equal(value, serializer.Deserialize<double>(json));
    }
}
