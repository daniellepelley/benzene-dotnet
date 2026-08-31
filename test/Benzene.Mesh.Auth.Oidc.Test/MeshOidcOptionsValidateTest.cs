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

    /// <summary>
    /// #286: the distinct-byte-count floor alone is defeated by a short block repeated to reach the
    /// 32-byte minimum - "ABCDEFGH" x 4 has exactly 8 distinct byte values (clearing
    /// <c>MinimumDistinctSigningKeyBytes</c>) but is really only 8 bytes (64 bits) of actual keyspace,
    /// repeating with period 8 across a 32-byte key - well under half the key length. This key signs
    /// both the CSRF state token and the session cookie (a deterministic function of {Email, Exp}), so
    /// this is a full session-forgery vector hiding behind a check whose doc comment claims a real
    /// generated secret "clears this by a wide margin."
    /// </summary>
    [Fact]
    public void EightByteRepeatingPatternPaddedTo32Bytes_Throws()
    {
        var options = Valid();
        options.SigningKey = string.Concat(System.Linq.Enumerable.Repeat("ABCDEFGH", 4));
        Assert.Equal(32, System.Text.Encoding.UTF8.GetByteCount(options.SigningKey));
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    /// <summary>
    /// The period-based check must not reject a key whose shortest repeating block is HALF the key's
    /// length (two tiles) - that is exactly the shape <see cref="SigningKeyWithEnoughDistinctBytes_IsAccepted"/>'s
    /// first case already asserts must be accepted ("0123456789abcdef" x 2), so this pins the boundary
    /// explicitly rather than leaving it to one shared theory case.
    /// </summary>
    [Fact]
    public void SixteenByteBlockRepeatedTwiceToFill32Bytes_IsAccepted()
    {
        var options = Valid();
        options.SigningKey = string.Concat(System.Linq.Enumerable.Repeat("0123456789abcdef", 2));
        Assert.Equal(32, System.Text.Encoding.UTF8.GetByteCount(options.SigningKey));
        options.Validate();
    }

    /// <summary>
    /// A longer key built from a short repeated block must still be caught even when the block itself
    /// has plenty of distinct bytes and the overall key is well over the 32-byte floor - the period
    /// check is a property of the WHOLE key, not just the 32-byte minimum case.
    /// </summary>
    [Fact]
    public void EightByteRepeatingPatternPaddedTo64Bytes_Throws()
    {
        var options = Valid();
        options.SigningKey = string.Concat(System.Linq.Enumerable.Repeat("ABCDEFGH", 8));
        Assert.Equal(64, System.Text.Encoding.UTF8.GetByteCount(options.SigningKey));
        Assert.Throws<ArgumentException>(() => options.Validate());
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

    // --- #244: port OAuth2BearerOptions' algorithm-confusion hardening (round 11 #174) here - mirrors
    // OAuth2BearerOptionsValidationTest's matrix (null/whitespace entry, "none", a typo'd algorithm
    // name). Prior to this fix, Validate() only checked ValidAlgorithms was non-empty; any of the
    // three shapes below was silently accepted at wire-up. -----------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidAlgorithmsContainingUselessEntry_Throws(string? badEntry)
    {
        var options = Valid();
        options.ValidAlgorithms = new[] { "RS256", badEntry! };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void ValidAlgorithmsContainingNone_Throws()
    {
        // RFC 8725 §3.1's canonical algorithm-confusion attack - "alg": "none" must never be a
        // wire-up-accepted allowlist entry, not just something the ID token itself can't claim its
        // way past.
        var options = Valid();
        options.ValidAlgorithms = new[] { "none" };

        var exception = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("none", exception.Message);
    }

    [Fact]
    public void ValidAlgorithmsContainingNoneAmongRealOnes_Throws()
    {
        var options = Valid();
        options.ValidAlgorithms = new[] { "RS256", "none" };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData("RS257")]
    [InlineData("rot13")]
    [InlineData("HMAC-SHA256")]
    public void ValidAlgorithmsContainingUnrecognizedName_Throws(string badAlgorithm)
    {
        var options = Valid();
        options.ValidAlgorithms = new[] { badAlgorithm };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData("RS256")]
    [InlineData("HS256")]
    [InlineData("RS384")]
    [InlineData("ES512")]
    [InlineData("PS256")]
    public void ValidAlgorithmsContainingRecognizedName_DoesNotThrow(string goodAlgorithm)
    {
        var options = Valid();
        options.ValidAlgorithms = new[] { goodAlgorithm };
        options.Validate();
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
