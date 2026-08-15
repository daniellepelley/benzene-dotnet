using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// Builds the single, long-lived <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;</c> used by
/// <see cref="Extensions.UseMeshOidcAuth{TContext}"/> - constructed once at pipeline wire-up time (not
/// per request), so OIDC discovery (<c>{Issuer}/.well-known/openid-configuration</c>) is fetched once
/// and cached/auto-refreshed from then on, giving <see cref="OpenIdConnectConfiguration.AuthorizationEndpoint"/>,
/// <see cref="OpenIdConnectConfiguration.TokenEndpoint"/>, and JWKS-based signing-key resolution
/// (<see cref="OpenIdConnectConfiguration.JsonWebKeySet"/>, wired into <c>TokenValidationParameters</c>)
/// without any provider-specific endpoint hardcoded in this package. Same building blocks as
/// <c>Benzene.Auth.OAuth2.OAuth2ConfigurationManagerFactory</c>'s <c>Authority</c> path.
/// </summary>
internal static class OidcConfigurationManagerFactory
{
    /// <summary>Creates a <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;</c> for the given,
    /// already-<see cref="MeshOidcOptions.Validate"/>-d options.</summary>
    public static ConfigurationManager<OpenIdConnectConfiguration> Create(MeshOidcOptions options)
    {
        var discoveryUrl = options.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var documentRetriever = new HttpDocumentRetriever { RequireHttps = options.RequireHttpsMetadata };

        return new ConfigurationManager<OpenIdConnectConfiguration>(
            discoveryUrl, new OpenIdConnectConfigurationRetriever(), documentRetriever);
    }
}
