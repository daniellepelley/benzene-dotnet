using Benzene.Avro;
using Xunit;

namespace Benzene.Test.Plugins.Avro;

/// <summary>
/// Regression tests for #57: Benzene.Avro has no schema-evolution support - the writer and reader
/// schema are the same resolved schema, so a field-count/order mismatch between the producer's and
/// consumer's version of a reflected type used to be silently wrong (field removed) or an opaque
/// low-level exception (field reordered) instead of a clear, catchable error. Both now throw
/// <see cref="AvroSchemaMismatchException"/>.
/// </summary>
public class AvroSchemaMismatchTest
{
    [Fact]
    public void FieldRemoved_PreviouslySilentlyWrong_NowThrowsSchemaMismatch()
    {
        // "Producer" serializes the three-field shape...
        var serializer = new AvroSerializer();
        var payload = serializer.Serialize(new ThreeFieldDto { A = "a", B = "b", C = "c" });

        // ...a "consumer" on a version with the middle field (B) removed reads the same bytes back.
        // Before the fix this silently bound C's bytes to A and B's bytes to C - no exception, just
        // wrong data. It must now be detected and reported clearly.
        var ex = Assert.Throws<AvroSchemaMismatchException>(() => serializer.Deserialize<TwoFieldDto>(payload));

        Assert.Equal(typeof(TwoFieldDto), ex.TargetType);
    }

    [Fact]
    public void FieldReordered_PreviouslyOpaqueException_NowThrowsSchemaMismatch()
    {
        // "Producer" serializes fields in A, B order...
        var serializer = new AvroSerializer();
        var payload = serializer.Serialize(new OrderedAbDto { A = 5, B = 7 });

        // ...a "consumer" on a version with the same fields declared in B, A order reads the same
        // bytes back. Before the fix this threw an opaque low-level exception (typically
        // IndexOutOfRangeException) with no indication of the actual cause.
        var ex = Assert.Throws<AvroSchemaMismatchException>(() => serializer.Deserialize<OrderedBaDto>(payload));

        Assert.Equal(typeof(OrderedBaDto), ex.TargetType);
    }

    [Fact]
    public void MatchingSchema_StillRoundTrips()
    {
        // The guard must not false-positive on the normal case: same type on both sides.
        var serializer = new AvroSerializer();
        var payload = serializer.Serialize(new ThreeFieldDto { A = "a", B = "b", C = "c" });

        var result = serializer.Deserialize<ThreeFieldDto>(payload);

        Assert.NotNull(result);
        Assert.Equal("a", result!.A);
        Assert.Equal("b", result.B);
        Assert.Equal("c", result.C);
    }
}
