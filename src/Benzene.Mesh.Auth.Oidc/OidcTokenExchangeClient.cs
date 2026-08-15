using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>Thrown when the server-to-server authorization-code-for-tokens exchange fails (non-2xx
/// response, or a 2xx response with no usable <c>id_token</c>). The message is diagnostic detail for
/// server-side logs only - callers must never return it to the browser (see
/// <see cref="OidcCallbackMiddleware{TContext}"/>'s "no detail leakage" handling).</summary>
internal sealed class OidcTokenExchangeException : Exception
{
    public OidcTokenExchangeException(string message) : base(message)
    {
    }

    public OidcTokenExchangeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Performs the OAuth2 Authorization Code flow's server-to-server token exchange: POSTs
/// <c>code</c>/<c>redirect_uri</c>/<c>client_id</c>/<c>client_secret</c>/<c>grant_type=authorization_code</c>
/// to the provider's token endpoint and extracts the returned <c>id_token</c>. Uses the same bare
/// <see cref="HttpClient"/> convention as <c>Benzene.Mesh.Dispatch.HttpMeshServiceDispatcher</c> - no
/// extra HTTP client library.
/// </summary>
internal sealed class OidcTokenExchangeClient
{
    private readonly HttpClient _httpClient;

    public OidcTokenExchangeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Exchanges an authorization <paramref name="code"/> for an ID token. Throws
    /// <see cref="OidcTokenExchangeException"/> on any failure - a non-2xx response, or a 2xx response
    /// with no string <c>id_token</c> property.</summary>
    public async Task<string> ExchangeCodeForIdTokenAsync(
        string tokenEndpoint, string clientId, string clientSecret, string code, string redirectUri)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(tokenEndpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new OidcTokenExchangeException(
                $"Token endpoint returned {(int)response.StatusCode} {response.StatusCode}.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new OidcTokenExchangeException("Token endpoint response was not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("id_token", out var idTokenElement) ||
                idTokenElement.ValueKind != JsonValueKind.String)
            {
                throw new OidcTokenExchangeException("Token endpoint response did not contain a string id_token.");
            }

            return idTokenElement.GetString()!;
        }
    }
}
