using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
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

    // #219: this MUST match the base URL this app is actually listening on - Startup.cs builds the
    // JWKS lookup URL (and OAuth2BearerOptions.ValidIssuers) directly from this constant. If Kestrel
    // ends up on a different port (a busy 5000, an explicit --urls, launchSettings.json, or
    // ASPNETCORE_URLS overriding it), every /protected/* request fails with a bare 401 and no other
    // clue why: the token's "iss" claim no longer matches ValidIssuers, and/or the JWKS fetch 404s.
    // If GET /protected/ping 401s and curl http://localhost:5000/demo-token works, check the port
    // your server actually bound (the console startup banner logs it) and update this constant.
    public const string Issuer = "http://localhost:5000/";
    public const string Audience = "benzene-example-asp";

    private readonly RSA _rsa = RSA.Create(2048);

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
