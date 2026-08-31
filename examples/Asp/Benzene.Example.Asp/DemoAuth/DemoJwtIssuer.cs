using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Benzene.Example.Asp.DemoAuth;

/// <summary>
/// A self-contained fake identity provider for demonstrating <c>Benzene.Auth.OAuth2</c> without
/// needing a real one: generates an RSA key at startup, serves it as a JWKS document
/// (<see cref="DemoAuthController"/>'s <c>/.well-known/jwks.json</c>), and mints demo tokens
/// signed with the same key (<c>/demo-token</c>). This is demo-only scaffolding for
/// <c>examples/Asp</c> - a real service points <see cref="Benzene.Auth.OAuth2.OAuth2BearerOptions"/>
/// at an actual identity provider instead. See docs/cookbooks/auth-patterns.md.
/// </summary>
public sealed class DemoJwtIssuer
{
    public const string KeyId = "demo-key-1";
    public const string Audience = "benzene-example-asp";

    private readonly RSA _rsa = RSA.Create(2048);

    /// <summary>
    /// The demo issuer's base URL, which <see cref="JwksUri"/> is derived from. Configurable
    /// via the "DEMO_AUTH_ISSUER" config value (config.json or an environment variable), defaulting
    /// to <c>http://localhost:5000/</c> to preserve this example's out-of-the-box behavior.
    ///
    /// This MUST match the base URL the app is actually served from: <c>OAuth2BearerOptions</c>
    /// fetches JWKS from <see cref="JwksUri"/> to validate a token's signature, and a mismatch (e.g.
    /// the app is run with `--urls http://localhost:5050` but this is left at the :5000 default)
    /// fails that fetch. The symptom is an opaque <c>401 Unauthorized</c> on every request to
    /// <c>/protected/*</c> with no message hinting at the real cause - if that happens, check this
    /// value against the port the app is actually listening on.
    /// </summary>
    public string Issuer { get; }

    /// <summary>The demo identity provider's JWKS endpoint, derived from <see cref="Issuer"/>.</summary>
    public string JwksUri => $"{Issuer}.well-known/jwks.json";

    public DemoJwtIssuer(IConfiguration configuration)
    {
        Issuer = configuration["DEMO_AUTH_ISSUER"] ?? "http://localhost:5000/";
    }

    public string JwksJson()
    {
        var parameters = _rsa.ExportParameters(false);
        var jwks = new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = KeyId,
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                }
            }
        };
        return JsonSerializer.Serialize(jwks);
    }

    /// <summary>Mints a demo token, valid for 10 minutes, carrying the given space-delimited scopes.</summary>
    public string IssueToken(string scope)
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object> { ["scope"] = scope, ["sub"] = "demo-user" }
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
