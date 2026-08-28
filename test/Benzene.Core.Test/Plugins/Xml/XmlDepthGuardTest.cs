using System.Text;
using Benzene.Core.Exceptions;
using Benzene.Xml;
using Xunit;

namespace Benzene.Test.Plugins.Xml;

/// <summary>
/// Regression tests for #260: <see cref="XmlSerializer.Deserialize{T}(string)"/> used to recurse
/// unboundedly on a self-referencing/deeply-nested request DTO, crashing the whole process with an
/// uncatchable CLR stack overflow - the identical bug class <c>Benzene.Avro</c>'s #56 guard already
/// closed for Avro. These tests assert the new depth guard trips with a catchable
/// <see cref="BenzeneException"/> well below any crash threshold; the crash itself can't safely be
/// reproduced in-process (it would kill the test run), so per the ruling this is the primary regression
/// coverage rather than an out-of-process harness proving the old crash is gone.
/// </summary>
public class XmlDepthGuardTest
{
    // Hand-builds `<XmlChainNode><Name>n0</Name><Next><Name>n1</Name><Next>...<Name>leaf</Name>
    // </Next>...</Next></XmlChainNode>` directly as text (`levels` <Next> wrappers), rather than going
    // through XmlSerializer.Serialize on a deep object graph - so the nesting depth of the payload
    // under test is completely decoupled from whatever the SERIALIZE side does (out of scope for
    // #260/this guard), and building the fixture itself can never recurse/stack-overflow regardless of
    // `levels`.
    private static string BuildNestedXml(int levels)
    {
        var sb = new StringBuilder();
        sb.Append("<XmlChainNode>");
        for (var i = 0; i < levels; i++)
        {
            sb.Append($"<Name>n{i}</Name><Next>");
        }

        sb.Append("<Name>leaf</Name>");
        for (var i = 0; i < levels; i++)
        {
            sb.Append("</Next>");
        }

        sb.Append("</XmlChainNode>");
        return sb.ToString();
    }

    [Fact]
    public void Deserialize_DeepSelfReferencingChain_ThrowsBenzeneException_NotStackOverflow()
    {
        // 200 levels of <Next> nesting - comfortably past the default MaxDepth of 32, and still nowhere
        // near a depth that would actually blow the CLR stack.
        var serializer = new XmlSerializer(new XmlOptions());
        var xml = BuildNestedXml(200);

        Assert.Throws<BenzeneException>(() => serializer.Deserialize<XmlChainNode>(xml));
    }

    [Fact]
    public void Deserialize_ConfiguredMaxDepth_TripsAtThatDepth()
    {
        // A tight, explicit MaxDepth makes the boundary deterministic: 20 levels is unambiguously past
        // a MaxDepth of 5.
        var serializer = new XmlSerializer(new XmlOptions { MaxDepth = 5 });
        var xml = BuildNestedXml(20);

        Assert.Throws<BenzeneException>(() => serializer.Deserialize<XmlChainNode>(xml));
    }

    [Fact]
    public void Deserialize_ShallowChain_WithinConfiguredMaxDepth_StillRoundTrips()
    {
        // Guards against a false-positive on ordinary shallow data: a single leaf node must round-trip
        // even under a tight MaxDepth.
        var serializer = new XmlSerializer(new XmlOptions { MaxDepth = 5 });
        var xml = BuildNestedXml(0);

        var result = serializer.Deserialize<XmlChainNode>(xml);

        Assert.NotNull(result);
        Assert.Equal("leaf", result!.Name);
        Assert.Null(result.Next);
    }

    [Fact]
    public void Deserialize_ModeratelyNestedChain_UnderDefaultMaxDepth_StillRoundTrips()
    {
        // 10 levels sits comfortably under the default 32 - the guard must not be so tight it breaks a
        // legitimately nested (if unusual) request shape such as a real comment/category tree.
        var serializer = new XmlSerializer();
        var xml = BuildNestedXml(10);

        var result = serializer.Deserialize<XmlChainNode>(xml);

        Assert.NotNull(result);
        Assert.Equal("n0", result!.Name);
        Assert.NotNull(result.Next);
    }

    [Fact]
    public void Deserialize_PayloadWithDoctype_StillRejected_NoEntityExpansion()
    {
        // Regression guard: adding the depth-guarded reader wrapper must not disturb the existing
        // entity-expansion/DTD-prohibited hardening (XmlSerializerTest covers this too; repeated here
        // against the depth-guard-wrapped path specifically).
        const string billionLaughs =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE XmlChainNode [" +
            "<!ENTITY a \"aaaaaaaaaa\">" +
            "<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">" +
            "]>" +
            "<XmlChainNode><Name>&b;</Name></XmlChainNode>";

        var serializer = new XmlSerializer();

        Assert.ThrowsAny<System.Exception>(() => serializer.Deserialize<XmlChainNode>(billionLaughs));
    }
}
