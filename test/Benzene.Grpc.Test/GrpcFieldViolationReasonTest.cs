using Google.Protobuf;
using Xunit;

namespace Benzene.Grpc.Test;

// wire-contracts §4.2 (spec 1.1): an error's code rides in google.rpc.BadRequest.FieldViolation.reason
// (field 3 of error_details.proto). Proven on the raw wire, independently of the helper's own reader, so
// a peer whose generated FieldViolation knows `reason` reads exactly what Benzene writes.
public class GrpcFieldViolationReasonTest
{
    [Fact]
    public void Create_WritesReasonAsField3_AndItSurvivesAnyPackingAndSerialization()
    {
        var badRequest = new Google.Rpc.BadRequest();
        badRequest.FieldViolations.Add(GrpcFieldViolationReason.Create("items[0].sku", "Sku must not be empty", "NotEmptyValidator"));
        var status = new Google.Rpc.Status { Code = 3 };
        status.Details.Add(Google.Protobuf.WellKnownTypes.Any.Pack(badRequest));

        var received = Google.Rpc.Status.Parser.ParseFrom(status.ToByteArray()).Details[0].Unpack<Google.Rpc.BadRequest>();
        var violation = Assert.Single(received.FieldViolations);

        Assert.Equal("items[0].sku", violation.Field);
        Assert.Equal("Sku must not be empty", violation.Description);
        Assert.Equal("NotEmptyValidator", GrpcFieldViolationReason.Get(violation));
        Assert.Equal("NotEmptyValidator", ReadField3(violation.ToByteArray()));
    }

    [Fact]
    public void Create_NoReason_WritesNoField3()
    {
        var violation = GrpcFieldViolationReason.Create("name", "Name is required", null);

        Assert.Null(ReadField3(violation.ToByteArray()));
        Assert.Null(GrpcFieldViolationReason.Get(violation));
    }

    [Fact]
    public void Get_ReadsAReasonWrittenByAnotherProducer()
    {
        // A FieldViolation as a peer with the newer generated type writes it: field 1, 2, 3.
        using var stream = new System.IO.MemoryStream();
        var output = new CodedOutputStream(stream);
        output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteString("orderId");
        output.WriteTag(2, WireFormat.WireType.LengthDelimited); output.WriteString("Order already exists");
        output.WriteTag(3, WireFormat.WireType.LengthDelimited); output.WriteString("duplicate-order");
        output.Flush();

        var violation = Google.Rpc.BadRequest.Types.FieldViolation.Parser.ParseFrom(stream.ToArray());

        Assert.Equal("duplicate-order", GrpcFieldViolationReason.Get(violation));
    }

    private static string? ReadField3(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        string? value = null;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 3) value = input.ReadString();
            else input.SkipLastField();
        }
        return value;
    }
}
