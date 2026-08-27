using System;
using Benzene.Abstractions.Middleware;
using Benzene.AspNet.Core;
using Benzene.Auth.OAuth2;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Auth;

/// <summary>
/// <see cref="OAuth2BearerOptions.Validate"/> runs at pipeline wire-up time (via
/// <c>UseOAuth2Bearer</c>), not on the first request - a misconfigured pipeline must fail fast.
/// These exercise it through the public <c>UseOAuth2Bearer</c> entry point rather than calling the
/// internal <c>Validate()</c> directly.
/// </summary>
public class OAuth2BearerOptionsValidationTest
{
    private static OAuth2BearerOptions ValidOptions()
    {
        return new OAuth2BearerOptions
        {
            JwksUri = "https://issuer.example.com/.well-known/jwks.json",
            ValidIssuers = new[] { "https://issuer.example.com" },
            ValidAudiences = new[] { "my-api" },
            ValidAlgorithms = new[] { "RS256" }
        };
    }

    private static IMiddlewarePipelineBuilder<AspNetContext> CreatePipelineBuilder()
    {
        var services = new ServiceCollection().ConfigureServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        return new MiddlewarePipelineBuilder<AspNetContext>(container);
    }

    [Fact]
    public void BothAuthorityAndJwksUriSet_Throws()
    {
        var options = ValidOptions();
        options.Authority = "https://issuer.example.com/.well-known/openid-configuration";

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Fact]
    public void NeitherAuthorityNorJwksUriSet_Throws()
    {
        var options = ValidOptions();
        options.JwksUri = null;

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Fact]
    public void EmptyValidIssuers_Throws()
    {
        var options = ValidOptions();
        options.ValidIssuers = Array.Empty<string>();

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Fact]
    public void EmptyValidAudiences_Throws()
    {
        var options = ValidOptions();
        options.ValidAudiences = Array.Empty<string>();

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Fact]
    public void EmptyValidAlgorithms_Throws()
    {
        // The one that directly guards against RFC 8725 §3.1 algorithm confusion - an empty
        // allowlist would trust whatever "alg" the token itself claims.
        var options = ValidOptions();
        options.ValidAlgorithms = Array.Empty<string>();

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Fact]
    public void ValidOptions_DoesNotThrow()
    {
        var app = CreatePipelineBuilder();
        app.UseOAuth2Bearer(ValidOptions());
    }

    // --- #174: the same bug class as round 1's #20 - a non-https Authority/JwksUri with
    // RequireHttpsMetadata true (the default) used to reach OAuth2ConfigurationManagerFactory
    // unvalidated, producing a permanent, silent 401 on every valid token at request time instead of
    // failing fast here. ---------------------------------------------------------------------------

    [Fact]
    public void HttpJwksUriWithDefaultRequireHttpsMetadata_Throws()
    {
        var options = ValidOptions();
        options.JwksUri = "http://issuer.example.com/.well-known/jwks.json";

        var app = CreatePipelineBuilder();
        var exception = Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));

        Assert.Contains("RequireHttpsMetadata", exception.Message);
        Assert.Contains("http://issuer.example.com", exception.Message);
    }

    [Fact]
    public void HttpAuthorityWithDefaultRequireHttpsMetadata_Throws()
    {
        var options = ValidOptions();
        options.JwksUri = null;
        options.Authority = "http://issuer.example.com/.well-known/openid-configuration";

        var app = CreatePipelineBuilder();
        var exception = Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));

        Assert.Contains("RequireHttpsMetadata", exception.Message);
    }

    [Fact]
    public void HttpJwksUriWithRequireHttpsMetadataFalse_DoesNotThrow()
    {
        var options = ValidOptions();
        options.JwksUri = "http://localhost:1234/.well-known/jwks.json";
        options.RequireHttpsMetadata = false;

        var app = CreatePipelineBuilder();
        app.UseOAuth2Bearer(options);
    }

    [Fact]
    public void HttpsJwksUri_DoesNotThrow()
    {
        var app = CreatePipelineBuilder();
        app.UseOAuth2Bearer(ValidOptions());
    }

    // --- #174: ValidIssuers/ValidAudiences null/whitespace/"*" entries --------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void ValidIssuersContainingUselessEntry_Throws(string? badEntry)
    {
        var options = ValidOptions();
        options.ValidIssuers = new[] { "https://issuer.example.com", badEntry! };

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void ValidAudiencesContainingUselessEntry_Throws(string? badEntry)
    {
        var options = ValidOptions();
        options.ValidAudiences = new[] { "my-api", badEntry! };

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    // --- #174: ValidAlgorithms "none" / unrecognized names --------------------------------------

    [Fact]
    public void ValidAlgorithmsContainingNone_Throws()
    {
        // RFC 8725 §3.1's canonical algorithm-confusion attack - "alg": "none" must never be a
        // wire-up-accepted allowlist entry, not just something the token itself can't claim its way past.
        var options = ValidOptions();
        options.ValidAlgorithms = new[] { "none" };

        var app = CreatePipelineBuilder();
        var exception = Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));

        Assert.Contains("none", exception.Message);
    }

    [Fact]
    public void ValidAlgorithmsContainingNoneAmongRealOnes_Throws()
    {
        var options = ValidOptions();
        options.ValidAlgorithms = new[] { "RS256", "none" };

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Theory]
    [InlineData("RS257")]
    [InlineData("rot13")]
    [InlineData("HMAC-SHA256")]
    public void ValidAlgorithmsContainingUnrecognizedName_Throws(string badAlgorithm)
    {
        var options = ValidOptions();
        options.ValidAlgorithms = new[] { badAlgorithm };

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Theory]
    [InlineData("HS256")]
    [InlineData("RS384")]
    [InlineData("ES512")]
    [InlineData("PS256")]
    public void ValidAlgorithmsContainingRecognizedName_DoesNotThrow(string goodAlgorithm)
    {
        var options = ValidOptions();
        options.ValidAlgorithms = new[] { goodAlgorithm };

        var app = CreatePipelineBuilder();
        app.UseOAuth2Bearer(options);
    }

    // --- #174: unbounded ClockSkew ---------------------------------------------------------------

    [Fact]
    public void ClockSkewOfTenYears_Throws()
    {
        var options = ValidOptions();
        options.ClockSkew = TimeSpan.FromDays(365 * 10);

        var app = CreatePipelineBuilder();
        var exception = Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));

        Assert.Contains(nameof(OAuth2BearerOptions.ClockSkew), exception.Message);
    }

    [Fact]
    public void NegativeClockSkew_Throws()
    {
        var options = ValidOptions();
        options.ClockSkew = TimeSpan.FromMinutes(-1);

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }

    [Fact]
    public void ClockSkewAtMaximum_DoesNotThrow()
    {
        var options = ValidOptions();
        options.ClockSkew = OAuth2BearerOptions.MaxClockSkew;

        var app = CreatePipelineBuilder();
        app.UseOAuth2Bearer(options);
    }

    [Fact]
    public void ClockSkewJustOverMaximum_Throws()
    {
        var options = ValidOptions();
        options.ClockSkew = OAuth2BearerOptions.MaxClockSkew + TimeSpan.FromSeconds(1);

        var app = CreatePipelineBuilder();
        Assert.Throws<ArgumentException>(() => app.UseOAuth2Bearer(options));
    }
}
