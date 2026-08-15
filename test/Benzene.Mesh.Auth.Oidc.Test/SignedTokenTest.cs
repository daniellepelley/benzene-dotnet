using System.Text;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class SignedTokenTest
{
    private static readonly byte[] Key1 = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    private static readonly byte[] Key2 = Encoding.UTF8.GetBytes("fedcba9876543210fedcba9876543210");

    private sealed record Payload(string Name, int Value);

    [Fact]
    public void RoundTrip_Succeeds()
    {
        var token = SignedToken.Create(Key1, new Payload("hello", 42));

        var ok = SignedToken.TryParse<Payload>(Key1, token, out var payload);

        Assert.True(ok);
        Assert.Equal("hello", payload!.Name);
        Assert.Equal(42, payload.Value);
    }

    [Fact]
    public void TamperedPayload_FailsVerification()
    {
        var token = SignedToken.Create(Key1, new Payload("hello", 42));
        var parts = token.Split('.');

        // Flip a single character in the payload segment - the signature no longer matches.
        var tamperedPayloadChar = parts[0][0] == 'a' ? 'b' : 'a';
        var tampered = tamperedPayloadChar + parts[0][1..] + "." + parts[1];

        var ok = SignedToken.TryParse<Payload>(Key1, tampered, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TamperedSignature_FailsVerification()
    {
        var token = SignedToken.Create(Key1, new Payload("hello", 42));
        var parts = token.Split('.');

        var tamperedSigChar = parts[1][0] == 'a' ? 'b' : 'a';
        var tampered = parts[0] + "." + tamperedSigChar + parts[1][1..];

        var ok = SignedToken.TryParse<Payload>(Key1, tampered, out _);

        Assert.False(ok);
    }

    [Fact]
    public void WrongKey_FailsVerification()
    {
        var token = SignedToken.Create(Key1, new Payload("hello", 42));

        var ok = SignedToken.TryParse<Payload>(Key2, token, out _);

        Assert.False(ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-token")]
    [InlineData("only-one-segment.")]
    [InlineData(".")]
    public void MalformedToken_FailsVerification(string? malformed)
    {
        var ok = SignedToken.TryParse<Payload>(Key1, malformed, out _);

        Assert.False(ok);
    }
}
