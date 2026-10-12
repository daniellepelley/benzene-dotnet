using Google.Protobuf;

namespace Benzene.Grpc;

/// <summary>
/// Reads and writes <c>google.rpc.BadRequest.FieldViolation.reason</c> - where wire-contracts.md §4.2
/// (spec 1.1) carries an error's <c>code</c> over gRPC: a server SHOULD write it there and a client
/// SHOULD read it back.
/// </summary>
/// <remarks>
/// <c>reason</c> is field 3 of <c>FieldViolation</c> in <c>google/rpc/error_details.proto</c>. The
/// <c>Google.Api.CommonProtos</c> version this package resolves (2.13.0, through <c>Grpc.StatusProto</c>)
/// predates it, and lifting it would force a <c>Google.Protobuf</c> upgrade on every consumer - so the field
/// is written and read on the wire directly. Protobuf preserves a field its generated type does not know
/// (unknown-field retention), so the value survives packing into <c>Any</c> and serializing, and a peer whose
/// generated type does know the field reads it as <c>reason</c>.
/// </remarks>
public static class GrpcFieldViolationReason
{
    /// <summary>The field number of <c>reason</c> in <c>google.rpc.BadRequest.FieldViolation</c>.</summary>
    public const int FieldNumber = 3;

    /// <summary>Builds a field violation carrying <paramref name="field"/>, <paramref name="description"/> and, when non-empty, <paramref name="reason"/>.</summary>
    /// <param name="field">The dotted field path (empty when the error has no field).</param>
    /// <param name="description">The error message.</param>
    /// <param name="reason">The error's code; null or empty writes no <c>reason</c> (proto3's unset).</param>
    public static Google.Rpc.BadRequest.Types.FieldViolation Create(string field, string description, string? reason)
    {
        var violation = new Google.Rpc.BadRequest.Types.FieldViolation { Field = field, Description = description };
        if (string.IsNullOrEmpty(reason))
        {
            return violation;
        }

        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        violation.WriteTo(output);
        output.WriteTag(FieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteString(reason);
        output.Flush();
        return Google.Rpc.BadRequest.Types.FieldViolation.Parser.ParseFrom(stream.ToArray());
    }

    /// <summary>Reads <c>reason</c> from <paramref name="violation"/>; null when absent or empty.</summary>
    /// <param name="violation">The received field violation.</param>
    public static string? Get(Google.Rpc.BadRequest.Types.FieldViolation violation)
    {
        var input = new CodedInputStream(violation.ToByteArray());
        string? reason = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == FieldNumber && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                reason = input.ReadString(); // last one wins, as protobuf's own parser does
            }
            else
            {
                input.SkipLastField();
            }
        }

        return string.IsNullOrEmpty(reason) ? null : reason;
    }
}
