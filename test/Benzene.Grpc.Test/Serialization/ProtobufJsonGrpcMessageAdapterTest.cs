using Benzene.Core.Exceptions;
using Benzene.Grpc.Serialization;
using Benzene.Grpc.Test.Protos;
using Xunit;

namespace Benzene.Grpc.Test.Serialization;

public class ProtobufJsonGrpcMessageAdapterTest
{
    private class EchoRequestPoco
    {
        public string Name { get; set; } = string.Empty;
    }

    private class EchoReplyPoco
    {
        public string? Message { get; set; }
    }

    private enum ColourPoco
    {
        COLOUR_UNSPECIFIED = 0,
        COLOUR_RED = 1,
        COLOUR_GREEN = 2
    }

    private class LongEnumPoco
    {
        public long BigValue { get; set; }
        public ulong UnsignedValue { get; set; }
        public ColourPoco Colour { get; set; }
    }

    // Deliberately has no public parameterless constructor (unlike a protoc-emitted message, or
    // EchoReplyPoco above) so Activator.CreateInstance<TResponse>() would throw the opaque
    // MissingMethodException the null-payload branch used to leak, absent the #48 fix.
    private class NoParameterlessCtorPoco
    {
        public NoParameterlessCtorPoco(string required)
        {
            Required = required;
        }

        public string Required { get; }
    }

    [Fact]
    public void ConvertRequest_WhenAlreadyTargetType_ReturnsSameInstance()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();
        var request = new EchoRequest { Name = "foo" };

        var result = adapter.ConvertRequest<EchoRequest>(request);

        Assert.Same(request, result);
    }

    [Fact]
    public void ConvertRequest_WhenNull_ReturnsNull()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();

        var result = adapter.ConvertRequest<EchoRequest>(null);

        Assert.Null(result);
    }

    [Fact]
    public void ConvertRequest_WhenProtobufMessage_ConvertsToPoco()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();
        var request = new EchoRequest { Name = "foo" };

        var result = adapter.ConvertRequest<EchoRequestPoco>(request);

        Assert.NotNull(result);
        Assert.Equal("foo", result!.Name);
    }

    [Fact]
    public void ConvertRequest_WhenProtobufMessageHasLongAndEnumFields_ConvertsToPoco()
    {
        // proto3 canonical JSON encodes int64/uint64 as strings and enums as name strings; the POCO
        // bridge must read those into long/ulong/enum properties rather than throwing (regression).
        var adapter = new ProtobufJsonGrpcMessageAdapter();
        var request = new LongEnumMessage
        {
            BigValue = 9_000_000_000L,
            UnsignedValue = 18_000_000_000UL,
            Colour = Colour.Green
        };

        var result = adapter.ConvertRequest<LongEnumPoco>(request);

        Assert.NotNull(result);
        Assert.Equal(9_000_000_000L, result!.BigValue);
        Assert.Equal(18_000_000_000UL, result.UnsignedValue);
        Assert.Equal(ColourPoco.COLOUR_GREEN, result.Colour);
    }

    [Fact]
    public void ConvertResponse_WhenAlreadyTargetType_ReturnsSameInstance()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();
        var reply = new EchoReply { Message = "hi" };

        var result = adapter.ConvertResponse<EchoReply>(reply);

        Assert.Same(reply, result);
    }

    [Fact]
    public void ConvertResponse_WhenPoco_ConvertsToProtobufMessage()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();
        var payload = new EchoReplyPoco { Message = "hi" };

        var result = adapter.ConvertResponse<EchoReply>(payload);

        Assert.Equal("hi", result.Message);
    }

    [Fact]
    public void ConvertResponse_WhenPocoHasNullProperty_UsesProtobufDefault()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();
        var payload = new EchoReplyPoco { Message = null };

        var result = adapter.ConvertResponse<EchoReply>(payload);

        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public void ConvertResponse_WhenPayloadIsNull_ReturnsAnEmptyResponseMessage()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();

        var result = adapter.ConvertResponse<EchoReply>(null);

        Assert.Equal(new EchoReply(), result);
    }

    [Fact]
    public void ConvertResponse_WhenTargetIsNotAProtobufMessage_ThrowsBenzeneException()
    {
        var adapter = new ProtobufJsonGrpcMessageAdapter();

        Assert.Throws<BenzeneException>(() => adapter.ConvertResponse<EchoReplyPoco>(new object()));
    }

    [Fact]
    public void ConvertResponse_WhenPayloadIsNullAndTargetIsNotAProtobufMessage_ThrowsTheSameClearBenzeneExceptionAsTheNonNullBranch()
    {
        // #48: the null-payload branch must be routed through the same "is this a real protobuf
        // message" check the non-null branch (above) already applies, and throw the SAME message shape -
        // not an opaque MissingMethodException from Activator.CreateInstance.
        var adapter = new ProtobufJsonGrpcMessageAdapter();

        var nonNullBranchException = Assert.Throws<BenzeneException>(
            () => adapter.ConvertResponse<NoParameterlessCtorPoco>(new object()));
        var nullBranchException = Assert.Throws<BenzeneException>(
            () => adapter.ConvertResponse<NoParameterlessCtorPoco>(null));

        Assert.Equal(nonNullBranchException.Message, nullBranchException.Message);
        Assert.Contains(nameof(NoParameterlessCtorPoco), nullBranchException.Message);
    }
}
