using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Benzene.Mesh.Auth.Oidc.Test.Fakes;

/// <summary>
/// A real loopback OIDC provider double: serves genuine OIDC discovery
/// (<c>/.well-known/openid-configuration</c>), a JWKS document, and a token endpoint that exchanges a
/// pre-registered authorization code for a pre-minted ID token - all over real HTTP, so
/// <c>Benzene.Mesh.Auth.Oidc</c>'s discovery/JWKS/token-exchange code is exercised for real, not mocked
/// away. The three provider-specific endpoints (<see cref="AuthorizationEndpoint"/>,
/// <see cref="TokenEndpoint"/>, <see cref="JwksUri"/>) live at deliberately non-Google-shaped paths
/// (under <c>/oidc/...</c>, not Google's real <c>/o/oauth2/v2/auth</c> /
/// <c>/oauth2/v3/certs</c> shapes) to prove the package genuinely reads them from the discovery
/// document rather than assuming Google's specific paths anywhere.
/// </summary>
public sealed class FakeOidcProvider : IDisposable
{
    private readonly HttpListener _listener;
    private readonly List<(string KeyId, RSA Rsa)> _keys = new();
    private readonly object _keysGate = new();
    private readonly ConcurrentDictionary<string, string> _tokenResponses = new();
    private readonly ConcurrentDictionary<string, int> _tokenErrors = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _authorizePath;
    private readonly string _tokenPath;
    private readonly string _jwksPath;
    private readonly string _discoveryPath = "/.well-known/openid-configuration";

    public string Issuer { get; }
    public string AuthorizationEndpoint { get; }
    public string TokenEndpoint { get; }
    public string JwksUri { get; }

    public int DiscoveryRequestCount;
    public int JwksRequestCount;
    public int TokenRequestCount;

    public FakeOidcProvider()
    {
        var port = GetFreeTcpPort();
        Issuer = $"http://localhost:{port}";
        _authorizePath = "/oidc/authorize";
        _tokenPath = "/oidc/token";
        _jwksPath = "/oidc/keys";
        AuthorizationEndpoint = Issuer + _authorizePath;
        TokenEndpoint = Issuer + _tokenPath;
        JwksUri = Issuer + _jwksPath;

        _listener = new HttpListener();
        _listener.Prefixes.Add(Issuer + "/");
        _listener.Start();
        _ = Task.Run(RunAsync);
    }

    /// <summary>Generates and registers a new RSA key, for signing tokens with <see cref="CreateToken"/>.</summary>
    public RSA AddKey(string keyId)
    {
        var rsa = RSA.Create(2048);
        lock (_keysGate)
        {
            _keys.Add((keyId, rsa));
        }

        return rsa;
    }

    /// <summary>Mints an RS256-signed ID token with the given issuer/audience/claims/lifetime. Callers
    /// pass <see cref="Issuer"/> explicitly (rather than this defaulting to it) so a wrong-issuer test
    /// case is straightforward to write.</summary>
    public static string CreateToken(
        RSA signingKey, string keyId, string issuer, string audience,
        DateTime? expires = null, DateTime? notBefore = null,
        IDictionary<string, object>? extraClaims = null)
    {
        var key = new RsaSecurityKey(signingKey) { KeyId = keyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>(extraClaims ?? new Dictionary<string, object>()),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Mints an HMAC-signed token - for proving the algorithm allowlist rejects it.</summary>
    public static string CreateHmacSignedToken(string issuer, string audience, string secret)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = credentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Registers a successful token-exchange response: a POST with <c>code={code}</c> to the
    /// token endpoint returns <c>{ id_token: idToken }</c>.</summary>
    public void RegisterTokenResponse(string code, string idToken) => _tokenResponses[code] = idToken;

    /// <summary>Registers a failing token-exchange response: a POST with <c>code={code}</c> returns the
    /// given non-2xx status.</summary>
    public void RegisterTokenError(string code, int statusCode) => _tokenErrors[code] = statusCode;

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                await RouteAsync(context);
            }
            catch (Exception)
            {
                // Best-effort - the client-side call observes the failure either way.
            }
        }
    }

    private async Task RouteAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;

        if (path == _discoveryPath)
        {
            Interlocked.Increment(ref DiscoveryRequestCount);
            await WriteJsonAsync(context, 200, BuildDiscoveryDocument());
            return;
        }

        if (path == _jwksPath)
        {
            Interlocked.Increment(ref JwksRequestCount);
            await WriteJsonAsync(context, 200, BuildJwksJson());
            return;
        }

        if (path == _tokenPath && context.Request.HttpMethod == "POST")
        {
            Interlocked.Increment(ref TokenRequestCount);
            await HandleTokenRequestAsync(context);
            return;
        }

        context.Response.StatusCode = 404;
        context.Response.OutputStream.Close();
    }

    private async Task HandleTokenRequestAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        var form = ParseFormBody(body);
        form.TryGetValue("code", out var code);
        code ??= string.Empty;

        if (_tokenErrors.TryGetValue(code, out var errorStatus))
        {
            context.Response.StatusCode = errorStatus;
            var errorBytes = Encoding.UTF8.GetBytes("{\"error\":\"invalid_grant\"}");
            await context.Response.OutputStream.WriteAsync(errorBytes);
            context.Response.OutputStream.Close();
            return;
        }

        if (_tokenResponses.TryGetValue(code, out var idToken))
        {
            await WriteJsonAsync(context, 200, JsonSerializer.Serialize(new { id_token = idToken, token_type = "Bearer" }));
            return;
        }

        context.Response.StatusCode = 400;
        var unknownBytes = Encoding.UTF8.GetBytes("{\"error\":\"invalid_grant\"}");
        await context.Response.OutputStream.WriteAsync(unknownBytes);
        context.Response.OutputStream.Close();
    }

    private static IDictionary<string, string> ParseFormBody(string body)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private string BuildDiscoveryDocument()
    {
        return JsonSerializer.Serialize(new
        {
            issuer = Issuer,
            authorization_endpoint = AuthorizationEndpoint,
            token_endpoint = TokenEndpoint,
            jwks_uri = JwksUri,
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        });
    }

    private string BuildJwksJson()
    {
        (string KeyId, RSA Rsa)[] snapshot;
        lock (_keysGate)
        {
            snapshot = _keys.ToArray();
        }

        var keys = new List<object>();
        foreach (var (keyId, rsa) in snapshot)
        {
            var parameters = rsa.ExportParameters(false);
            keys.Add(new
            {
                kty = "RSA",
                use = "sig",
                kid = keyId,
                alg = "RS256",
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent),
            });
        }

        return JsonSerializer.Serialize(new { keys });
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.OutputStream.Close();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        foreach (var (_, rsa) in _keys)
        {
            rsa.Dispose();
        }
    }
}
