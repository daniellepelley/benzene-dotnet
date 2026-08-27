using System;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class MeshOidcOptionsValidateTest
{
    // A real-looking, high-entropy 32-byte key - NOT new string('k', 32): that repeated-character shape
    // is exactly the #177 vulnerability this file's own LowEntropySigningKey_Throws test below covers,
    // so the baseline "valid" fixture must not accidentally be an example of it.
    private const string HighEntropySigningKey = "Q7f#kL9$mP2@xR5&nW8!zV3^tY6*bU1(";

    private static MeshOidcOptions Valid() => new()
    {
        Issuer = "https://accounts.google.com",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        SigningKey = HighEntropySigningKey,
        AllowedEmails = new[] { "user@example.com" },
    };

    [Fact]
    public void ValidOptions_DoNotThrow()
    {
        var options = Valid();
        options.Validate();
    }

    [Fact]
    public void MissingIssuer_Throws()
    {
        var options = Valid();
        options.Issuer = "";
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    /// <summary>
    /// #173 / round 1's #20: a non-https issuer used to reach OIDC discovery unvalidated and crash with
    /// an unhandled 500 the first time discovery metadata was actually fetched - mid-request, not at
    /// startup. Mirrors <c>deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.Validate</c>'s identical ruling for
    /// the identical gap.
    /// </summary>
    [Fact]
    public void NonHttpsIssuerWithRequireHttpsMetadataTrue_Throws()
    {
        var options = Valid();
        options.Issuer = "http://accounts.example.com";
        Assert.True(options.RequireHttpsMetadata);
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void NonHttpsIssuerWithRequireHttpsMetadataFalse_DoesNotThrow()
    {
        // The documented test-only escape hatch: a loopback fake provider (this project's own
        // FakeOidcProvider) has no TLS in front of it.
        var options = Valid();
        options.Issuer = "http://localhost:12345";
        options.RequireHttpsMetadata = false;
        options.Validate();
    }

    [Fact]
    public void HttpsIssuer_WithRequireHttpsMetadataTrue_DoesNotThrow()
    {
        var options = Valid();
        options.Issuer = "https://accounts.example.com";
        options.Validate();
    }

    [Fact]
    public void MissingClientId_Throws()
    {
        var options = Valid();
        options.ClientId = "";
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void MissingClientSecret_Throws()
    {
        var options = Valid();
        options.ClientSecret = "";
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void MissingSigningKey_Throws()
    {
        var options = Valid();
        options.SigningKey = "";
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void ShortSigningKey_Throws()
    {
        // 32 bytes is the minimum - one byte short must fail, not silently accept a weak secret.
        var options = Valid();
        options.SigningKey = new string('k', 31);
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void SigningKeyExactly32Bytes_IsAccepted()
    {
        var options = Valid();
        options.SigningKey = HighEntropySigningKey;
        Assert.Equal(32, System.Text.Encoding.UTF8.GetByteCount(options.SigningKey));
        options.Validate();
    }

    /// <summary>
    /// #177: the exact vulnerability this fix closes - byte length alone let a 32-character REPEATED
    /// character straight through, and that key signs a session cookie that is otherwise a deterministic
    /// function of {Email, Exp} with no randomness of its own, making it a complete session-forgery
    /// vector. This used to be accepted (see this file's git history: the old version of
    /// <c>SigningKeyExactly32Bytes_IsAccepted</c> asserted exactly this shape did NOT throw).
    /// </summary>
    [Fact]
    public void LowEntropyRepeatedCharacterSigningKey_Throws()
    {
        var options = Valid();
        options.SigningKey = new string('k', 32);
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void LowEntropyShortAlternatingPatternSigningKey_Throws()
    {
        // 32 bytes, but only two distinct byte values stretched to meet the length check.
        var options = Valid();
        options.SigningKey = string.Concat(System.Linq.Enumerable.Repeat("ab", 16));
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")] // 16 distinct hex chars, repeated twice
    [InlineData("Tr0ub4dor&3-correct-horse-battery")] // a real mixed-character passphrase shape
    public void SigningKeyWithEnoughDistinctBytes_IsAccepted(string signingKey)
    {
        var options = Valid();
        options.SigningKey = signingKey;
        options.Validate();
    }

    [Fact]
    public void EmptyAllowedEmails_DoesNotThrow()
    {
        // A legitimate (if extreme) "lock everyone out" configuration, not a startup error.
        var options = Valid();
        options.AllowedEmails = Array.Empty<string>();
        options.Validate();
    }

    [Fact]
    public void BasePathNotAbsolute_Throws()
    {
        var options = Valid();
        options.BasePath = "mesh/auth";
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void EmptyValidAlgorithms_Throws()
    {
        var options = Valid();
        options.ValidAlgorithms = Array.Empty<string>();
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("mesh-ui")]
    [InlineData("")]
    public void HomePathThatIsNotASameOriginAbsolutePath_Throws(string homePath)
    {
        // HomePath is a redirect target (post-logout, and the login fallback), so a misconfigured
        // absolute/protocol-relative value would turn every logout into an open redirect.
        var options = Valid();
        options.HomePath = homePath;
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/mesh-ui")]
    public void SameOriginAbsoluteHomePath_IsAccepted(string homePath)
    {
        var options = Valid();
        options.HomePath = homePath;
        options.Validate();
    }
}
