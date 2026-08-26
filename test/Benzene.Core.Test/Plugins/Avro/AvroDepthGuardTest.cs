using System;
using System.IO;
using Avro.IO;
using Benzene.Avro;
using Xunit;

namespace Benzene.Test.Plugins.Avro;

/// <summary>
/// Regression tests for #56: Avro serialize/deserialize used to recurse unboundedly on a
/// self-referencing/deeply-nested schema or object graph, crashing the whole process with an
/// uncatchable CLR stack overflow (confirmed at ~15,000-16,000 nesting levels on deserialize,
/// ~100,000 on serialize). These tests assert the new depth guard trips with a catchable
/// <see cref="AvroPayloadTooDeepException"/> well below those crash thresholds - the crash itself
/// can't safely be reproduced in-process (it would kill the test run), so per the ruling this is the
/// primary regression coverage rather than an out-of-process harness proving the old crash is gone.
/// </summary>
public class AvroDepthGuardTest
{
    // ---------- deserialize side (the HIGH-priority, attacker-controlled vector) ----------

    // Hand-encodes a chain of `levels` non-leaf Node records followed by one leaf (Next = null),
    // without going through AvroSerializer.Serialize - so the depth of the payload under test is
    // completely decoupled from the SERIALIZE-side guard under test elsewhere in this file. Field
    // order/union branch order (["null", X], so index 0 = null, index 1 = the real branch) matches
    // AvroSchemaGenerator's reflected schema for Node { string Name; Node? Next; }.
    private static byte[] BuildNodeChainBytes(int levels)
    {
        using var ms = new MemoryStream();
        var encoder = new BinaryEncoder(ms);
        for (var i = 0; i < levels; i++)
        {
            encoder.WriteUnionIndex(1); // Name: non-null branch
            encoder.WriteString($"n{i}");
            encoder.WriteUnionIndex(1); // Next: non-null branch -> one more nested Node follows
        }

        encoder.WriteUnionIndex(1); // leaf Name
        encoder.WriteString("leaf");
        encoder.WriteUnionIndex(0); // leaf Next: null -> chain terminates
        encoder.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Deserialize_DeepSelfReferencingChain_ThrowsAvroPayloadTooDeepException_NotStackOverflow()
    {
        // 300 levels * 2 ReadUnionIndex calls/level = 600, past the default MaxDepth of 500 - and
        // still nowhere near the ~15,000+ level threshold that actually blows the CLR stack.
        var serializer = new AvroSerializer(new AvroOptions());
        var bytes = BuildNodeChainBytes(300);
        var base64 = Convert.ToBase64String(bytes);

        var ex = Assert.Throws<AvroPayloadTooDeepException>(() => serializer.Deserialize<Node>(base64));

        Assert.Equal(AvroOptions.DefaultMaxDepth, ex.MaxDepth);
        Assert.True(ex.Depth > ex.MaxDepth);
    }

    [Fact]
    public void Deserialize_ConfiguredMaxDepth_TripsAtThatDepth()
    {
        // A tight, explicit MaxDepth makes the boundary deterministic: 20 levels (44 ReadUnionIndex
        // calls) is unambiguously past a MaxDepth of 5.
        var serializer = new AvroSerializer(new AvroOptions { MaxDepth = 5 });
        var bytes = BuildNodeChainBytes(20);
        var base64 = Convert.ToBase64String(bytes);

        var ex = Assert.Throws<AvroPayloadTooDeepException>(() => serializer.Deserialize<Node>(base64));

        Assert.Equal(5, ex.MaxDepth);
    }

    [Fact]
    public void Deserialize_ShallowChain_WithinConfiguredMaxDepth_StillRoundTrips()
    {
        // Guards against a false-positive on ordinary shallow data: a single leaf node (2
        // ReadUnionIndex calls) must round-trip even under a tight MaxDepth.
        var serializer = new AvroSerializer(new AvroOptions { MaxDepth = 5 });
        var bytes = BuildNodeChainBytes(0);
        var base64 = Convert.ToBase64String(bytes);

        var result = serializer.Deserialize<Node>(base64);

        Assert.NotNull(result);
        Assert.Equal("leaf", result!.Name);
        Assert.Null(result.Next);
    }

    [Fact]
    public void Deserialize_ModeratelyDeepChain_UnderDefaultMaxDepth_StillRoundTrips()
    {
        // 100 levels (202 calls) sits comfortably under the default 500 - the guard must not be so
        // tight it breaks legitimately nested (if unusual) data.
        var serializer = new AvroSerializer(new AvroOptions());
        var bytes = BuildNodeChainBytes(100);
        var base64 = Convert.ToBase64String(bytes);

        var result = serializer.Deserialize<Node>(base64);

        Assert.NotNull(result);
        Assert.Equal("n0", result!.Name);
    }

    // ---------- serialize side (less urgent - usually not attacker-controlled - but still a crash risk) ----------

    // Builds the chain iteratively (not recursively), so constructing the object graph itself can
    // never stack-overflow regardless of `levels`.
    private static Node BuildNodeChainObject(int levels)
    {
        var node = new Node { Name = "leaf" };
        for (var i = 0; i < levels; i++)
        {
            node = new Node { Name = $"n{i}", Next = node };
        }

        return node;
    }

    [Fact]
    public void Serialize_DeepSelfReferencingObjectGraph_ThrowsAvroPayloadTooDeepException()
    {
        var serializer = new AvroSerializer(new AvroOptions { MaxDepth = 10 });
        var chain = BuildNodeChainObject(50);

        var ex = Assert.Throws<AvroPayloadTooDeepException>(() => serializer.Serialize(chain));

        Assert.Equal(10, ex.MaxDepth);
    }

    [Fact]
    public void Serialize_ModeratelyDeepObjectGraph_UnderConfiguredMaxDepth_StillRoundTrips()
    {
        var serializer = new AvroSerializer(new AvroOptions { MaxDepth = 500 });
        var chain = BuildNodeChainObject(100);

        var result = serializer.Deserialize<Node>(serializer.Serialize(chain));

        Assert.NotNull(result);
        Assert.Equal("n99", result!.Name);
    }
}
