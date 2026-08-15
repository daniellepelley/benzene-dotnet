using System;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class MeshOidcOptionsValidateTest
{
    private static MeshOidcOptions Valid() => new()
    {
        Issuer = "https://accounts.google.com",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        SigningKey = new string('k', 32),
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
        options.SigningKey = new string('k', 32);
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
}
