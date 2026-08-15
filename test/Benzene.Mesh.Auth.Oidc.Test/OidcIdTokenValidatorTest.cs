using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Benzene.Mesh.Auth.Oidc.Test.Fakes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

/// <summary>
/// Exercises ID token verification end to end against a real loopback OIDC provider
/// (<see cref="FakeOidcProvider"/>) - genuine HTTP discovery + JWKS fetch + signature verification, not
/// mocked-away validation logic. Also proves discovery is actually read (not hardcoded) since the fake
/// provider's endpoints live at deliberately non-Google-shaped paths.
/// </summary>
public class OidcIdTokenValidatorTest
{
    private const string Audience = "test-client-id";

    private static (OidcIdTokenValidator Validator, FakeOidcProvider Provider, RSA Key) CreateValidator(
        string[]? validAlgorithms = null)
    {
        var provider = new FakeOidcProvider();
        var key = provider.AddKey("kid1");

        var configurationManager = OidcConfigurationManagerFactory.Create(new MeshOidcOptions
        {
            Issuer = provider.Issuer,
            ClientId = Audience,
            ClientSecret = "secret",
            SigningKey = new string('k', 32),
            RequireHttpsMetadata = false, // loopback HTTP fake, never done for a real provider
        });

        var validationParameters = new TokenValidationParameters
        {
            ValidIssuers = Extensions.ValidIssuersFor(provider.Issuer),
            ValidAudiences = new[] { Audience },
            ValidAlgorithms = validAlgorithms ?? new[] { "RS256" },
            ClockSkew = TimeSpan.FromMinutes(2),
            ConfigurationManager = configurationManager,
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
        };

        var validator = new OidcIdTokenValidator(new JsonWebTokenHandler(), validationParameters);
        return (validator, provider, key);
    }

    [Fact]
    public async Task ValidToken_WithVerifiedEmail_Succeeds()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        var token = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, Audience, extraClaims: new Dictionary<string, object>
        {
            ["email"] = "User@Example.com",
            ["email_verified"] = true,
        });

        var result = await validator.ValidateAsync(token);

        Assert.True(result.IsValid);
        // Lowercased - allowlist comparison downstream is also case-insensitive, but normalizing here
        // means the session cookie and every log line agree on one canonical form.
        Assert.Equal("user@example.com", result.Email);
    }

    [Fact]
    public async Task EmailVerifiedFalse_Fails()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        var token = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, Audience, extraClaims: new Dictionary<string, object>
        {
            ["email"] = "user@example.com",
            ["email_verified"] = false,
        });

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task EmailVerifiedMissing_FailsClosed()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        // No email_verified claim at all - must NOT be treated as verified.
        var token = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, Audience, extraClaims: new Dictionary<string, object>
        {
            ["email"] = "user@example.com",
        });

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ExpiredToken_Fails()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        var token = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, Audience,
            expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-10),
            extraClaims: new Dictionary<string, object> { ["email"] = "user@example.com", ["email_verified"] = true });

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WrongIssuer_Fails()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        var token = FakeOidcProvider.CreateToken(key, "kid1", "https://a-different-issuer.example.com", Audience,
            extraClaims: new Dictionary<string, object> { ["email"] = "user@example.com", ["email_verified"] = true });

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WrongAudience_Fails()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        var token = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, "a-different-audience",
            extraClaims: new Dictionary<string, object> { ["email"] = "user@example.com", ["email_verified"] = true });

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task DisallowedAlgorithm_HmacSignedToken_Fails()
    {
        // The algorithm-confusion test: only RS256 is allowed, but this token is HMAC-signed. A
        // validator trusting the token's own "alg" claim would need something to treat as the HMAC
        // secret - proving the explicit allowlist (not the token's self-declared alg) is what's enforced.
        var (validator, provider, _) = CreateValidator(new[] { "RS256" });
        using var _ = provider;

        var token = FakeOidcProvider.CreateHmacSignedToken(provider.Issuer, Audience, "some-shared-secret-value-at-least-32-bytes-long");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task BadSignature_Fails()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        // Sign with a DIFFERENT key than the one published in the JWKS - the classic forged-signature case.
        using var wrongKey = RSA.Create(2048);
        var token = FakeOidcProvider.CreateToken(wrongKey, "kid1", provider.Issuer, Audience,
            extraClaims: new Dictionary<string, object> { ["email"] = "user@example.com", ["email_verified"] = true });

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task DiscoveryIsReadFromDocument_NotHardcoded()
    {
        // FakeOidcProvider's authorization/token/jwks endpoints live at deliberately non-Google-shaped
        // paths (/oidc/...) - a successful validation here is only possible if the JWKS URI was
        // genuinely read out of the discovery document rather than assumed.
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        Assert.Contains("/oidc/", provider.JwksUri);
        Assert.DoesNotContain("googleapis.com", provider.JwksUri);

        var token = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, Audience,
            extraClaims: new Dictionary<string, object> { ["email"] = "user@example.com", ["email_verified"] = true });

        var result = await validator.ValidateAsync(token);

        Assert.True(result.IsValid);
        Assert.True(provider.DiscoveryRequestCount >= 1);
        Assert.True(provider.JwksRequestCount >= 1);
    }

    [Fact]
    public async Task DiscoveryDocument_IsCachedNotRefetchedPerRequest()
    {
        var (validator, provider, key) = CreateValidator();
        using var _ = provider;

        var token1 = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, Audience,
            extraClaims: new Dictionary<string, object> { ["email"] = "a@example.com", ["email_verified"] = true });
        var token2 = FakeOidcProvider.CreateToken(key, "kid1", provider.Issuer, Audience,
            extraClaims: new Dictionary<string, object> { ["email"] = "b@example.com", ["email_verified"] = true });

        await validator.ValidateAsync(token1);
        await validator.ValidateAsync(token2);

        // Two validations, but ConfigurationManager<T>'s own caching means the discovery document (and
        // the JWKS it points to) is fetched once and reused - not once per validation.
        Assert.Equal(1, provider.DiscoveryRequestCount);
        Assert.Equal(1, provider.JwksRequestCount);
    }
}
