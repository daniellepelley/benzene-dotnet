using Benzene.Aws.Lambda.S3;
using Xunit;

namespace Benzene.Test.Aws.S3;

public class S3ObjectKeyCodecTest
{
    // #191: AsS3 never encoded the key it was given, so a test-constructed key containing a
    // reserved character was corrupted once the real getter (S3MessageBodyGetter) ran it through
    // S3ObjectKeyCodec.Decode - e.g. "invoice+2024-08-27.pdf" came back as "invoice 2024-08-27.pdf".
    // Encode is the fix; these tests assert Encode/Decode are true inverses over the reserved
    // character set S3's own scheme treats specially (space/'+', '%', and non-ASCII), property-style,
    // plus the exact key from the finding.
    [Theory]
    [InlineData("invoice+2024-08-27.pdf")]
    [InlineData("plain-key")]
    [InlineData("key with spaces")]
    [InlineData("100% done.txt")]
    [InlineData("a+b c%d")]
    [InlineData("folder/sub+folder/file&name.txt")]
    [InlineData("café/naïve.txt")]
    [InlineData("日本語.json")]
    [InlineData("")]
    [InlineData("100%")]
    [InlineData("50%+off")]
    public void EncodeThenDecode_RoundTripsToTheOriginalKey(string rawKey)
    {
        var encoded = S3ObjectKeyCodec.Encode(rawKey);
        var decoded = S3ObjectKeyCodec.Decode(encoded);

        Assert.Equal(rawKey, decoded);
    }

    [Fact]
    public void EncodeThenDecode_TheInvoiceKeyFromTheFinding_RoundTripsByteExact()
    {
        // The exact repro from #191.
        const string rawKey = "invoice+2024-08-27.pdf";

        Assert.Equal(rawKey, S3ObjectKeyCodec.Decode(S3ObjectKeyCodec.Encode(rawKey)));
    }

    [Fact]
    public void Encode_SpaceBecomesPlusSign_MatchingS3sOwnEncodingScheme()
    {
        // S3's own URL-encoding scheme encodes a space as '+', not '%20' - the specific quirk #191
        // calls out. A generic Uri.EscapeDataString would get this wrong (and wouldn't be undone by
        // Decode, which relies on WebUtility.UrlDecode's '+' -> space behaviour).
        Assert.Equal("a+b", S3ObjectKeyCodec.Encode("a b"));
    }

    [Fact]
    public void Encode_Null_ReturnsNull()
    {
        Assert.Null(S3ObjectKeyCodec.Encode(null));
    }

    [Fact]
    public void Decode_Null_ReturnsNull()
    {
        Assert.Null(S3ObjectKeyCodec.Decode(null));
    }
}
