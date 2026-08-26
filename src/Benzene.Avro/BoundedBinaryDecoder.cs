using System.Text;
using Avro.IO;
using AvroDecoder = global::Avro.IO.Decoder;

namespace Benzene.Avro;

/// <summary>
/// An Avro <see cref="Decoder"/> that wraps a <see cref="BinaryDecoder"/> and guards two independent
/// hostile-payload vectors <em>before</em> the underlying third-party reader (Apache.Avro's
/// <c>PreresolvingDatumReader</c>) can act on attacker-controlled numbers:
/// <list type="bullet">
/// <item><b>Allocation size</b> - a length-prefixed <c>bytes</c>/<c>string</c> field whose declared
/// length exceeds a bound is rejected before a buffer of that length is allocated. The bound is the
/// decoded input size (no legitimate field can be longer than the whole message) tightened by
/// <see cref="AvroOptions.MaxDeserializeBytes"/> when set. This stops the classic "tiny input declaring
/// a huge length prefix drives a large allocation" OOM.</item>
/// <item><b>Recursion depth</b> - a self-referencing or very deeply-nested schema recurses one call
/// stack frame deeper on every nested union branch/array/map the reader follows, entirely inside
/// Apache.Avro's own reader (this Benzene package's converter code never runs deeply enough to see it).
/// Left unbounded, a payload well under 100 KB can recurse tens of thousands of levels deep and crash
/// the whole process with an uncatchable <see cref="StackOverflowException"/>. This counts every
/// nested-read entry point the decoder observes (<see cref="ReadUnionIndex"/> - the "pick a branch,
/// then possibly recurse into a nested record" pattern that a self-referencing type's reflected schema
/// always goes through - plus <see cref="ReadArrayStart"/>/<see cref="ReadMapStart"/> for an explicit
/// schema's non-nullable nested collections) and throws <see cref="AvroPayloadTooDeepException"/> once
/// <see cref="AvroOptions.MaxDepth"/> is exceeded, mirroring <c>MessagePackSecurity.UntrustedData</c>'s
/// depth cap. The count is never decremented (this decoder has no visibility into when the reader
/// returns from a nested record - the Avro <see cref="Decoder"/> interface has no such hook), so it is
/// a conservative bound on "how many nested reads has this whole payload triggered", not a precise
/// current-recursion-depth counter - see <see cref="AvroOptions.MaxDepth"/> for what that trades off.
/// </item>
/// </list>
/// Everything else delegates unchanged; <c>fixed</c> fields are sized by the schema (not the payload).
/// </summary>
internal sealed class BoundedBinaryDecoder : AvroDecoder
{
    private readonly BinaryDecoder _inner;
    private readonly long _maxLength;
    private readonly int _maxDepth;
    private int _depth;

    public BoundedBinaryDecoder(BinaryDecoder inner, long maxLength, int maxDepth = AvroOptions.DefaultMaxDepth)
    {
        _inner = inner;
        _maxLength = maxLength;
        _maxDepth = maxDepth;
    }

    private int ReadGuardedLength()
    {
        var length = _inner.ReadLong();
        if (length < 0 || length > _maxLength)
        {
            throw new AvroPayloadTooLargeException(length, _maxLength);
        }

        return (int)length;
    }

    private void GuardDepth()
    {
        if (++_depth > _maxDepth)
        {
            throw new AvroPayloadTooDeepException(_depth, _maxDepth, "deserializing");
        }
    }

    public byte[] ReadBytes()
    {
        // Avro `bytes` is [long length][length data]. Read the length, guard it, then read the data
        // via ReadFixed - replicating BinaryDecoder.ReadBytes but with the length check before allocation.
        var length = ReadGuardedLength();
        var buffer = new byte[length];
        _inner.ReadFixed(buffer, 0, length);
        return buffer;
    }

    public string ReadString()
    {
        // Avro `string` is [long length][length UTF-8 bytes] - same guard as ReadBytes.
        var length = ReadGuardedLength();
        var buffer = new byte[length];
        _inner.ReadFixed(buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }

    // Everything below delegates to the inner decoder unchanged.
    public void ReadNull() => _inner.ReadNull();
    public bool ReadBoolean() => _inner.ReadBoolean();
    public int ReadInt() => _inner.ReadInt();
    public long ReadLong() => _inner.ReadLong();
    public float ReadFloat() => _inner.ReadFloat();
    public double ReadDouble() => _inner.ReadDouble();
    public int ReadEnum() => _inner.ReadEnum();

    // ReadArrayStart/ReadMapStart/ReadUnionIndex are the three points at which the underlying reader
    // can recurse one level deeper into nested content (a non-nullable nested array/map under an
    // explicit schema, or - the reflection schema's own pattern for any nested record - picking a
    // union branch that turns out to be a record). Guard depth here, before delegating.
    public long ReadArrayStart()
    {
        GuardDepth();
        return _inner.ReadArrayStart();
    }

    public long ReadArrayNext() => _inner.ReadArrayNext();

    public long ReadMapStart()
    {
        GuardDepth();
        return _inner.ReadMapStart();
    }

    public long ReadMapNext() => _inner.ReadMapNext();

    public int ReadUnionIndex()
    {
        GuardDepth();
        return _inner.ReadUnionIndex();
    }

    public void ReadFixed(byte[] buffer) => _inner.ReadFixed(buffer);
    public void ReadFixed(byte[] buffer, int start, int length) => _inner.ReadFixed(buffer, start, length);
    public void SkipNull() => _inner.SkipNull();
    public void SkipBoolean() => _inner.SkipBoolean();
    public void SkipInt() => _inner.SkipInt();
    public void SkipLong() => _inner.SkipLong();
    public void SkipFloat() => _inner.SkipFloat();
    public void SkipDouble() => _inner.SkipDouble();
    public void SkipBytes() => _inner.SkipBytes();
    public void SkipString() => _inner.SkipString();
    public void SkipEnum() => _inner.SkipEnum();
    public void SkipUnionIndex() => _inner.SkipUnionIndex();
    public void SkipFixed(int len) => _inner.SkipFixed(len);
}
